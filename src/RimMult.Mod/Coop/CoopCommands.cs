using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using RimMult.Shared.Coop;
using RimWorld;
using UnityEngine;
using Verse;

namespace RimMult.Coop;

/// <summary>
/// Guests' orders. A guest's game is only a copy, so instead of acting on it, the order is described (which
/// designator on which cells, which right-click option by label, which gizmo by label…) and the host finds the
/// same designator/option/gizmo in the real game and uses it.
/// </summary>
internal static class CoopCommands
{
    private static readonly AccessTools.FieldRef<Game, sbyte> CurrentMapIndex = AccessTools.FieldRefAccess<Game, sbyte>("currentMapIndex");
    private static readonly AccessTools.FieldRef<Designator_Build, ThingDef?> BuildStuff = AccessTools.FieldRefAccess<Designator_Build, ThingDef?>("stuffDef");
    private static readonly AccessTools.FieldRef<Designator_Place, Rot4> PlacingRot = AccessTools.FieldRefAccess<Designator_Place, Rot4>("placingRot");
    private static readonly AccessTools.FieldRef<Pawn_WorkSettings, Pawn> WorkSettingsPawn = AccessTools.FieldRefAccess<Pawn_WorkSettings, Pawn>("pawn");

    private const string WorldPrefix = "world:";

    private static int _lastGizmoFrame = -1;
    private static string _lastGizmoKey = "";

    /// <summary>Host, while a guest's order runs: whether the queue key (Shift) counts as held, as it was for the guest.</summary>
    public static bool? QueueOverride { get; private set; }

    /// <summary>Guest: whether the queue key (Shift) is held for the order being given.</summary>
    public static bool QueueHeld()
    {
        try
        {
            return KeyBindingDefOf.QueueOrder.IsDownEvent || KeyBindingDefOf.QueueOrder.IsDown;
        }
        catch (Exception)
        {
            return false;
        }
    }

    // ---------- guest side: describe and send ----------

    public static void SendDesignate(Designator designator, IEnumerable<IntVec3>? cells, Thing? thing)
    {
        var command = new CoopCommand
        {
            Kind = CoopCommandKind.Designate,
            MapId = Find.CurrentMap.uniqueID,
            Name = designator.GetType().FullName ?? "",
        };
        if (designator is Designator_Place place)
        {
            command.Detail = place.PlacingDef?.defName ?? "";
            command.Number = PlacingRot(place).AsInt;
        }
        if (designator is Designator_Build build)
            command.Extra = BuildStuff(build)?.defName ?? "";
        // Installing works on the selected minified thing (or building to reinstall): the host selects the same one.
        if (designator is Designator_Install)
            command.Extra = Find.Selector.SingleSelectedThing?.thingIDNumber.ToString() ?? "";
        // Expanding or clearing an allowed area works on the area picked in the menu.
        if (designator is Designator_AreaAllowed)
            command.Number = Designator_AreaAllowed.SelectedArea?.ID ?? -1;
        if (cells != null)
            foreach (var cell in cells)
            {
                command.Cells.Add(cell.x);
                command.Cells.Add(cell.z);
            }
        if (thing != null)
            command.ThingIds.Add(thing.thingIDNumber);
        Send(command);
    }

    public static void SendFloatMenu(FloatMenuOption option, List<Pawn> pawns, Vector3 clickPos)
    {
        Send(new CoopCommand
        {
            Kind = CoopCommandKind.FloatMenu,
            MapId = Find.CurrentMap.uniqueID,
            ThingIds = pawns.Select(p => p.thingIDNumber).ToList(),
            Name = option.Label,
            X = clickPos.x,
            Z = clickPos.z,
            Queue = QueueHeld(),
        });
    }

    public static void SendGizmo(Command gizmo)
    {
        // In the globe view the buttons belong to the selected caravans and settlements.
        var worldObjects = CoopGlobe.SelectedWorldObjects();
        var selected = worldObjects.Count > 0 ? new List<Thing>() : Find.Selector.SelectedObjects.OfType<Thing>().ToList();
        var key = gizmo.GetType().FullName + "|" + gizmo.Label;

        // Grouped gizmos (three pawns, one "draft" button) are processed once per member in the same frame:
        // the host already applies the order to every selected thing, so send it once.
        if (Time.frameCount == _lastGizmoFrame && key == _lastGizmoKey)
            return;
        _lastGizmoFrame = Time.frameCount;
        _lastGizmoKey = key;

        Send(new CoopCommand
        {
            Kind = CoopCommandKind.Gizmo,
            MapId = Find.CurrentMap?.uniqueID ?? -1,
            ThingIds = selected.Select(t => t.thingIDNumber).ToList(),
            Name = gizmo.GetType().FullName ?? "",
            Detail = gizmo.Label ?? "",
            // A zone's buttons (allow sowing, …): the zone has no thing id, it goes by its own.
            Number = selected.Count == 0 && worldObjects.Count == 0 && Find.Selector.SelectedZone is { } zone ? zone.ID : -1,
            Extra = worldObjects.Count > 0 ? WorldPrefix + string.Join(",", worldObjects.Select(o => o.ID)) : "",
            Queue = QueueHeld(),
        });
    }

    public static void SendWorkPriority(Pawn_WorkSettings settings, WorkTypeDef work, int priority)
    {
        var pawn = WorkSettingsPawn(settings);
        Send(new CoopCommand
        {
            Kind = CoopCommandKind.WorkPriority,
            MapId = pawn.MapHeld?.uniqueID ?? -1,
            ThingIds = { pawn.thingIDNumber },
            Name = work.defName,
            Number = priority,
        });
    }

    public static void SendResearch(ResearchProjectDef? project) =>
        Send(new CoopCommand { Kind = CoopCommandKind.Research, Name = project?.defName ?? "" });

    public static void Send(CoopCommand command) =>
        CoopGuest.SendToHost(CoopChannel.Command, command.Encode());

    // ---------- host side: find and run ----------

    public static void Execute(int guestId, byte[] data)
    {
        CoopCommand command;
        try
        {
            command = CoopCommand.Decode(data);
        }
        catch (Exception e)
        {
            Log.Warning($"[RimMult] Co-op: bad command from player {guestId}: {e.Message}");
            return;
        }

        var map = Find.Maps.FirstOrDefault(m => m.uniqueID == command.MapId);
        var game = Current.Game;
        var previousMap = CurrentMapIndex(game);
        // Whatever the order opens (a dialog, a menu) would pop up on the host's screen: it is closed again.
        var windows = new HashSet<Window>(Find.WindowStack.Windows);
        QueueOverride = command.Queue;
        // Zone designators grow the selected zone: the host's own selection must not be what the guest's zone grows.
        var selection = command.Kind == CoopCommandKind.Designate ? Find.Selector.SelectedObjects.ToList() : null;
        if (selection != null)
            Find.Selector.ClearSelection();
        try
        {
            // Designators, menus and gizmos all work on "the current map": act on the guest's map without
            // moving the host's camera there.
            if (map != null)
                CurrentMapIndex(game) = (sbyte)map.Index;

            switch (command.Kind)
            {
                case CoopCommandKind.Designate when map != null:
                    Designate(command, map);
                    break;
                case CoopCommandKind.FloatMenu when map != null:
                    FloatMenu(command, map);
                    break;
                case CoopCommandKind.Gizmo:
                    if (Gizmo(command, map))
                        // The button opens a window or starts aiming: that happens on the guest's screen instead.
                        Multiplayer.Session?.SendCoop(CoopChannel.RunLocally, data, guestId);
                    break;
                case CoopCommandKind.WorkPriority:
                    WorkPriority(command);
                    break;
                case CoopCommandKind.Research:
                    Find.ResearchManager.SetCurrentProject(DefDatabase<ResearchProjectDef>.GetNamedSilentFail(command.Name));
                    break;
                case CoopCommandKind.Target when map != null:
                    CoopTargeting.Execute(command, map);
                    break;
                case CoopCommandKind.Edit:
                    CoopEdits.Apply(command, map);
                    break;
                case CoopCommandKind.CaravanGoto:
                case CoopCommandKind.WorldFloatMenu:
                case CoopCommandKind.FormCaravan:
                    CoopGlobe.Execute(command, map);
                    break;
            }
        }
        catch (Exception e)
        {
            Log.Warning($"[RimMult] Co-op: command {command.Kind} '{command.Name}' from player {guestId} failed: {e}");
        }
        finally
        {
            QueueOverride = null;
            CurrentMapIndex(game) = previousMap;
            if (selection != null)
            {
                Find.Selector.ClearSelection();
                foreach (var selected in selection)
                    Find.Selector.Select(selected, playSound: false, forceDesignatorDeselect: false);
            }
            foreach (var opened in Find.WindowStack.Windows.Where(w => !windows.Contains(w)).ToList())
                Find.WindowStack.TryRemove(opened, doCloseSound: false);
        }
    }

    private static void Designate(CoopCommand command, Map map)
    {
        var designator = FindDesignator(command, map);
        if (designator == null)
        {
            Log.Warning($"[RimMult] Co-op: no designator {command.Name} {command.Detail} here");
            return;
        }

        // The architect's designators are the host's own: their rotation and stuff are put back afterwards.
        var place = designator as Designator_Place;
        var build = designator as Designator_Build;
        var oldRot = place != null ? PlacingRot(place) : Rot4.North;
        var oldStuff = build != null ? BuildStuff(build) : null;
        var oldArea = Designator_AreaAllowed.selectedArea;
        if (designator is Designator_AreaAllowed)
            Designator_AreaAllowed.selectedArea = CoopEdits.HostArea(map, command.Number);
        if (place != null)
            PlacingRot(place) = new Rot4(command.Number);
        if (build != null && command.Extra.Length > 0)
            BuildStuff(build) = DefDatabase<ThingDef>.GetNamedSilentFail(command.Extra);
        try
        {
            Apply(designator, command, map);
        }
        finally
        {
            if (place != null)
                PlacingRot(place) = oldRot;
            if (build != null)
                BuildStuff(build) = oldStuff;
            Designator_AreaAllowed.selectedArea = oldArea;
        }
    }

    private static void Apply(Designator designator, CoopCommand command, Map map)
    {
        var byId = ThingsById(map);
        foreach (var id in command.ThingIds)
        {
            if (byId.TryGetValue(id, out var thing) && designator.CanDesignateThing(thing).Accepted)
                designator.DesignateThing(thing);
        }

        var cells = new List<IntVec3>();
        for (var i = 0; i + 1 < command.Cells.Count; i += 2)
        {
            var cell = new IntVec3(command.Cells[i], 0, command.Cells[i + 1]);
            if (cell.InBounds(map) && designator.CanDesignateCell(cell).Accepted)
                cells.Add(cell);
        }
        if (cells.Count == 1)
            designator.DesignateSingleCell(cells[0]);
        else if (cells.Count > 1)
            designator.DesignateMultiCell(cells);
    }

    /// <summary>
    /// The host's instance of the guest's designator: from the architect menu (dropdown groups included), or made
    /// anew for the ones that come from gizmos (build copy, install, mods' tools).
    /// </summary>
    private static Designator? FindDesignator(CoopCommand command, Map map)
    {
        if (command.Name == typeof(Designator_Install).FullName)
        {
            if (!int.TryParse(command.Extra, out var id) || !ThingsById(map).TryGetValue(id, out var toInstall))
                return null;
            Find.Selector.Select(toInstall, playSound: false, forceDesignatorDeselect: false);
            return new Designator_Install();
        }

        var known = DefDatabase<DesignationCategoryDef>.AllDefs
            .SelectMany(c => c.AllResolvedDesignators)
            .SelectMany(d => d is Designator_Dropdown dropdown ? dropdown.Elements.Prepend(d) : new[] { d })
            .FirstOrDefault(d => d.GetType().FullName == command.Name
                                 && (command.Detail.Length == 0 || (d as Designator_Place)?.PlacingDef?.defName == command.Detail));
        if (known != null)
            return known;

        if (command.Name == typeof(Designator_Build).FullName)
        {
            var def = (BuildableDef?)DefDatabase<ThingDef>.GetNamedSilentFail(command.Detail) ?? DefDatabase<TerrainDef>.GetNamedSilentFail(command.Detail);
            return def != null ? new Designator_Build(def) : null;
        }

        var type = AccessTools.TypeByName(command.Name);
        if (type == null || !typeof(Designator).IsAssignableFrom(type) || type.IsAbstract || AccessTools.Constructor(type, Type.EmptyTypes) == null)
            return null;
        return (Designator)Activator.CreateInstance(type);
    }

    private static void FloatMenu(CoopCommand command, Map map)
    {
        var byId = ThingsById(map);
        var pawns = command.ThingIds.Select(id => byId.TryGetValue(id, out var t) ? t as Pawn : null).Where(p => p != null).Cast<Pawn>().ToList();
        if (pawns.Count == 0)
            return;
        var options = FloatMenuMakerMap.GetOptions(pawns, new Vector3(command.X, 0f, command.Z), out _);
        var option = options.FirstOrDefault(o => o.Label == command.Name && !o.Disabled);
        option?.Chosen(colonistOrdering: true, floatMenu: null);
        foreach (var pawn in pawns)
            CoopHost.Touch(pawn);
    }

    /// <summary>Presses the guest's button; true when it opened a window or started aiming (the guest does that part).</summary>
    private static bool Gizmo(CoopCommand command, Map? map)
    {
        var windows = new HashSet<Window>(Find.WindowStack.Windows);
        var targeting = Find.Targeter.IsTargeting;
        var worldTargeting = Find.WorldTargeter.IsTargeting;

        if (command.ThingIds.Count == 0 && command.Number >= 0 && map?.zoneManager.AllZones.FirstOrDefault(z => z.ID == command.Number) is { } zone)
        {
            Press(zone.GetGizmos());
            CoopHost.TouchZones(map);
        }
        foreach (var worldObject in CoopGlobe.WorldObjectsByIds(WorldIds(command)))
        {
            Press(worldObject.GetGizmos());
            if (Find.WindowStack.Windows.Any(w => !windows.Contains(w)))
                break;
        }

        var all = Find.Maps.SelectMany(ThingsById).GroupBy(p => p.Key).ToDictionary(g => g.Key, g => g.First().Value);
        foreach (var id in command.ThingIds)
        {
            if (!all.TryGetValue(id, out var thing))
                continue;
            Press(thing.GetGizmos());
            // The guest wants to see the result right away (a bed made medical, a door held open, …).
            CoopHost.Touch(thing);
            if (Find.WindowStack.Windows.Any(w => !windows.Contains(w)))
                break; // a window per selected thing would only open again on the guest's side
        }

        var local = Find.WindowStack.Windows.Any(w => !windows.Contains(w));
        if (!targeting && Find.Targeter.IsTargeting)
        {
            Find.Targeter.StopTargeting();
            local = true;
        }
        if (!worldTargeting && Find.WorldTargeter.IsTargeting)
        {
            Find.WorldTargeter.StopTargeting();
            local = true;
        }
        return local;

        void Press(IEnumerable<Gizmo> gizmos)
        {
            var gizmo = gizmos.OfType<Command>().FirstOrDefault(g => g.GetType().FullName == command.Name && g.Label == command.Detail);
            if (gizmo != null && !gizmo.Disabled)
                gizmo.ProcessInput(new Event());
        }
    }

    /// <summary>Guest: the host sent this button back: it opens a window or starts aiming, so it runs here.</summary>
    public static void RunLocally(byte[] data)
    {
        CoopCommand command;
        try
        {
            command = CoopCommand.Decode(data);
        }
        catch (Exception)
        {
            return;
        }
        var map = Find.Maps.FirstOrDefault(m => m.uniqueID == command.MapId);
        IEnumerable<Gizmo> gizmos;
        if (WorldIds(command).Count > 0)
        {
            gizmos = CoopGlobe.WorldObjectsByIds(WorldIds(command)).SelectMany(o => o.GetGizmos());
        }
        else if (command.ThingIds.Count == 0 && command.Number >= 0 && map?.zoneManager.AllZones.FirstOrDefault(z => z.ID == command.Number) is { } zone)
        {
            gizmos = zone.GetGizmos();
        }
        else
        {
            var all = Find.Maps.SelectMany(ThingsById).GroupBy(p => p.Key).ToDictionary(g => g.Key, g => g.First().Value);
            gizmos = command.ThingIds.Select(id => all.TryGetValue(id, out var t) ? t : null).Where(t => t != null).SelectMany(t => t!.GetGizmos());
        }
        var gizmo = gizmos.OfType<Command>().FirstOrDefault(g => g.GetType().FullName == command.Name && g.Label == command.Detail);
        if (gizmo == null)
            return;
        Patches.CoopGizmoPatch.RunningLocally = true;
        try
        {
            CoopTargeting.Pressed(gizmo);
            gizmo.ProcessInput(new Event());
        }
        catch (Exception e)
        {
            Log.Warning($"[RimMult] Co-op: button '{gizmo.Label}' failed here: {e.Message}");
        }
        finally
        {
            Patches.CoopGizmoPatch.RunningLocally = false;
        }
    }

    private static void WorkPriority(CoopCommand command)
    {
        var work = DefDatabase<WorkTypeDef>.GetNamedSilentFail(command.Name);
        if (work == null)
            return;
        foreach (var map in Find.Maps)
        {
            var byId = ThingsById(map);
            foreach (var id in command.ThingIds)
            {
                if (byId.TryGetValue(id, out var thing) && thing is Pawn { workSettings: not null } pawn)
                {
                    pawn.workSettings.SetPriority(work, command.Number);
                    CoopHost.Touch(pawn);
                }
            }
        }
    }

    /// <summary>The world objects (caravans, settlements) a guest's button was pressed for.</summary>
    private static List<int> WorldIds(CoopCommand command) =>
        command.Kind == CoopCommandKind.Gizmo && command.Extra.StartsWith(WorldPrefix, StringComparison.Ordinal)
            ? command.Extra.Substring(WorldPrefix.Length).Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(s => int.TryParse(s, out var id) ? id : -1).Where(id => id >= 0).ToList()
            : new List<int>();

    public static Dictionary<int, Thing> ThingsById(Map map)
    {
        var byId = new Dictionary<int, Thing>();
        foreach (var thing in map.listerThings.AllThings)
        {
            if (thing.thingIDNumber >= 0)
                byId[thing.thingIDNumber] = thing;
        }
        return byId;
    }
}
