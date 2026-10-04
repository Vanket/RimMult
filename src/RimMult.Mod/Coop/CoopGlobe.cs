using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Xml;
using HarmonyLib;
using RimMult.Shared.Coop;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace RimMult.Coop;

/// <summary>
/// The globe in co-op: the colony's caravans move on guests' globes as they do on the host's, world objects that
/// appear or vanish (a caravan formed, a quest site, a destroyed settlement) follow, and a guest's caravan orders
/// (go there, the right-click menu, forming a caravan, caravan buttons) are carried out by the host.
/// </summary>
internal static class CoopGlobe
{
    private const float ContentInterval = 2f;

    private static readonly AccessTools.FieldRef<Caravan_PathFollower, bool> Moving = AccessTools.FieldRefAccess<Caravan_PathFollower, bool>("moving");
    private static readonly AccessTools.FieldRef<Caravan_PathFollower, bool> Paused = AccessTools.FieldRefAccess<Caravan_PathFollower, bool>("paused");
    private static readonly AccessTools.FieldRef<Dialog_FormCaravan, Map> FormMap = AccessTools.FieldRefAccess<Dialog_FormCaravan, Map>("map");
    private static readonly AccessTools.FieldRef<Dialog_FormCaravan, PlanetTile> FormDestination = AccessTools.FieldRefAccess<Dialog_FormCaravan, PlanetTile>("destinationTile");
    private static readonly Regex PatherNode = new("<pather>.*?</pather>", RegexOptions.Singleline | RegexOptions.Compiled);

    private static readonly Dictionary<int, int> ContentHash = new();
    private static HashSet<int>? _knownIds;
    private static string? _positionsKey;
    private static float _lastContent;

    /// <summary>A guest just loaded the game (it has the globe as it is now).</summary>
    public static void Forget()
    {
        ContentHash.Clear();
        _knownIds = null;
        _positionsKey = null;
        _lastContent = float.NegativeInfinity;
    }

    // ---------- host ----------

    public static void Build(CoopWorld world)
    {
        var caravans = Find.WorldObjects.Caravans.Where(c => c.IsPlayerControlled).ToList();
        var positions = caravans.Select(Position).ToList();
        var key = string.Join(";", positions.Select(p => $"{p.Id},{p.Tile},{p.NextTile},{p.CostLeft:F1},{p.Moving},{p.Paused}"));
        if (key != _positionsKey)
        {
            _positionsKey = key;
            world.Caravans = positions;
        }

        var all = Find.WorldObjects.AllWorldObjects;
        var ids = new HashSet<int>(all.Select(o => o.ID));
        if (_knownIds != null)
        {
            world.RemovedWorldObjects.AddRange(_knownIds.Where(id => !ids.Contains(id)));
            foreach (var added in all.Where(o => !_knownIds.Contains(o.ID)))
            {
                if (Save(added) is { } xml)
                {
                    world.WorldObjects.Add(xml);
                    ContentHash[added.ID] = ContentHashOf(xml);
                }
            }
        }
        _knownIds = ids;

        // What is inside the colony's caravans (pawns, their health and gear, the cargo) now and then.
        var now = Time.realtimeSinceStartup;
        if (now - _lastContent < ContentInterval)
            return;
        _lastContent = now;
        foreach (var caravan in caravans)
        {
            if (Save(caravan) is not { } xml)
                continue;
            var hash = ContentHashOf(xml);
            if (ContentHash.TryGetValue(caravan.ID, out var old) && old != hash && !world.WorldObjects.Contains(xml))
                world.WorldObjects.Add(xml);
            ContentHash[caravan.ID] = hash;
        }
    }

    private static CaravanPosition Position(Caravan caravan) => new()
    {
        Id = caravan.ID,
        Tile = caravan.Tile.ToString(),
        NextTile = caravan.pather.nextTile.ToString(),
        PreviousTile = caravan.pather.previousTileForDrawingIfInDoubt.ToString(),
        CostLeft = caravan.pather.nextTileCostLeft,
        CostTotal = caravan.pather.nextTileCostTotal,
        Moving = Moving(caravan.pather),
        Paused = Paused(caravan.pather),
    };

    /// <summary>The movement is sent apart (often), so it doesn't count as a change of what the caravan is.</summary>
    private static int ContentHashOf(string xml)
    {
        var content = PatherNode.Replace(xml, "");
        return content.GetHashCode() ^ content.Length;
    }

    /// <summary>
    /// A world object as a fragment. A caravan's pawns live among the host's world pawns and the caravan only refers
    /// to them: they are saved inside it instead (a guest doesn't have them).
    /// </summary>
    private static string? Save(WorldObject worldObject)
    {
        var caravan = worldObject as Caravan;
        var mode = caravan?.pawns.contentsLookMode ?? LookMode.Deep;
        if (caravan != null)
            caravan.pawns.contentsLookMode = LookMode.Deep;
        try
        {
            return ScribeMemory.Save(() => Scribe_Deep.Look(ref worldObject, "li"));
        }
        catch (Exception e)
        {
            Log.WarningOnce($"[RimMult] Co-op: could not save {worldObject}: {e.Message}", worldObject.ID ^ 0x434F574F);
            return null;
        }
        finally
        {
            if (caravan != null)
                caravan.pawns.contentsLookMode = mode;
        }
    }

    // ---------- guest ----------

    public static void Apply(CoopWorld world)
    {
        foreach (var id in world.RemovedWorldObjects)
        {
            if (Find.WorldObjects.AllWorldObjects.FirstOrDefault(o => o.ID == id) is { } gone)
                Find.WorldObjects.Remove(gone);
        }
        foreach (var xml in world.WorldObjects)
            Load(xml);
        if (world.Caravans != null)
            foreach (var position in world.Caravans)
                Move(position);
    }

    private static void Load(string xml)
    {
        try
        {
            var root = ScribeMemory.Parse(xml);
            if (root["li"] is not { } li || !int.TryParse(li["ID"]?.InnerText, out var id))
                return;
            ScribeMemory.StripHostOnlyReferences(li);
            var old = Find.WorldObjects.AllWorldObjects.FirstOrDefault(o => o.ID == id);
            var replaced = new HashSet<string>();
            if (old != null)
                replaced.Add(old.GetUniqueLoadID());
            foreach (XmlNode inner in li.SelectNodes(".//id")!)
                if (inner.InnerText.Length > 0)
                    replaced.Add("Thing_" + inner.InnerText);

            WorldObject? loaded = null;
            ScribeMemory.Load(root, replaced, () => Scribe_Deep.Look(ref loaded, "li"));
            if (loaded == null)
                return;
            var selected = old != null && Find.WorldSelector.IsSelected(old);
            if (old != null)
                Find.WorldObjects.Remove(old);
            Find.WorldObjects.Add(loaded);
            if (loaded is Caravan caravan)
                caravan.tweener.ResetTweenedPosToRoot();
            if (selected)
                Find.WorldSelector.Select(loaded, playSound: false);
        }
        catch (Exception e)
        {
            Log.WarningOnce($"[RimMult] Co-op: could not load a world object from the host: {e}", xml.GetHashCode());
        }
    }

    private static void Move(CaravanPosition position)
    {
        if (Find.WorldObjects.Caravans.FirstOrDefault(c => c.ID == position.Id) is not { } caravan
            || !PlanetTile.TryParse(position.Tile, out var tile))
            return;
        if (caravan.Tile != tile)
            caravan.Tile = tile;
        if (PlanetTile.TryParse(position.NextTile, out var next))
            caravan.pather.nextTile = next;
        if (PlanetTile.TryParse(position.PreviousTile, out var previous))
            caravan.pather.previousTileForDrawingIfInDoubt = previous;
        caravan.pather.nextTileCostLeft = position.CostLeft;
        caravan.pather.nextTileCostTotal = position.CostTotal;
        Moving(caravan.pather) = position.Moving;
        Paused(caravan.pather) = position.Paused;
        caravan.tweener.ResetTweenedPosToRoot();
    }

    // ---------- guest: orders ----------

    private sealed class Origin
    {
        public Origin(int caravanId, PlanetTile tile)
        {
            CaravanId = caravanId;
            Tile = tile;
        }

        public int CaravanId { get; }
        public PlanetTile Tile { get; }
    }

    private static readonly ConditionalWeakTable<FloatMenuOption, Origin> Origins = new();

    public static void RememberOptions(List<FloatMenuOption> options, PlanetTile tile, Caravan caravan)
    {
        var origin = new Origin(caravan.ID, tile);
        foreach (var option in options)
        {
            Origins.Remove(option);
            Origins.Add(option, origin);
        }
    }

    /// <summary>Guest: a globe menu option was picked; true when it went to the host.</summary>
    public static bool TryRelayChosen(FloatMenuOption option)
    {
        if (!Origins.TryGetValue(option, out var origin))
            return false;
        if (!option.Disabled)
            CoopCommands.Send(new CoopCommand
            {
                Kind = CoopCommandKind.WorldFloatMenu,
                ThingIds = { origin.CaravanId },
                Name = option.Label,
                Detail = origin.Tile.ToString(),
            });
        return true;
    }

    public static void SendGoto(Caravan caravan, PlanetTile tile) =>
        CoopCommands.Send(new CoopCommand { Kind = CoopCommandKind.CaravanGoto, ThingIds = { caravan.ID }, Detail = tile.ToString() });

    public static void SendFormCaravan(Dialog_FormCaravan dialog)
    {
        var map = FormMap(dialog);
        var command = new CoopCommand
        {
            Kind = CoopCommandKind.FormCaravan,
            MapId = map?.uniqueID ?? -1,
            Detail = FormDestination(dialog).ToString(),
        };
        foreach (var transferable in dialog.transferables)
        {
            var left = transferable.CountToTransfer;
            foreach (var thing in transferable.things)
            {
                if (left <= 0)
                    break;
                var count = Math.Min(left, thing.stackCount);
                command.Cells.Add(thing.thingIDNumber);
                command.Cells.Add(count);
                left -= count;
            }
        }
        CoopCommands.Send(command);
    }

    /// <summary>Guest: the world objects a caravan button works on (in the globe view).</summary>
    public static List<WorldObject> SelectedWorldObjects() =>
        WorldRendererUtility.WorldSelected ? Find.WorldSelector.SelectedObjects.ToList() : new List<WorldObject>();

    // ---------- host: carrying the orders out ----------

    public static void Execute(CoopCommand command, Map? map)
    {
        switch (command.Kind)
        {
            case CoopCommandKind.CaravanGoto when CaravanById(command) is { } caravan && PlanetTile.TryParse(command.Detail, out var tile):
                caravan.pather.StartPath(tile, null, repathImmediately: true);
                break;
            case CoopCommandKind.WorldFloatMenu when CaravanById(command) is { } caravan && PlanetTile.TryParse(command.Detail, out var tile):
                FloatMenuMakerWorld.ChoicesAtFor(tile, caravan).FirstOrDefault(o => o.Label == command.Name && !o.Disabled)?.Chosen(colonistOrdering: true, floatMenu: null);
                break;
            case CoopCommandKind.FormCaravan when map != null:
                FormCaravan(command, map);
                break;
        }
    }

    private static Caravan? CaravanById(CoopCommand command) =>
        command.ThingIds.Count > 0 ? Find.WorldObjects.Caravans.FirstOrDefault(c => c.ID == command.ThingIds[0] && c.IsPlayerControlled) : null;

    public static IEnumerable<WorldObject> WorldObjectsByIds(IEnumerable<int> ids)
    {
        var wanted = new HashSet<int>(ids);
        return Find.WorldObjects.AllWorldObjects.Where(o => wanted.Contains(o.ID));
    }

    /// <summary>Fills the host's own "form caravan" window with the guest's choice and sends the caravan off.</summary>
    private static void FormCaravan(CoopCommand command, Map map)
    {
        var wanted = new Dictionary<int, int>();
        for (var i = 0; i + 1 < command.Cells.Count; i += 2)
            wanted[command.Cells[i]] = command.Cells[i + 1];

        var dialog = new Dialog_FormCaravan(map);
        AccessTools.Method(typeof(Dialog_FormCaravan), "CalculateAndRecacheTransferables").Invoke(dialog, null);
        foreach (var transferable in dialog.transferables)
        {
            var count = transferable.things.Sum(t => wanted.TryGetValue(t.thingIDNumber, out var c) ? Math.Min(c, t.stackCount) : 0);
            transferable.ForceTo(transferable.ClampAmount(count));
        }
        if (PlanetTile.TryParse(command.Detail, out var destination) && destination.Valid)
            dialog.Notify_ChoseRoute(destination);
        var formed = (bool)AccessTools.Method(typeof(Dialog_FormCaravan), "TryFormAndSendCaravan").Invoke(dialog, null);
        if (!formed)
            Log.Warning("[RimMult] Co-op: the guest's caravan could not be formed here.");
    }
}

/// <summary>Guest: right-click "go there" on the globe goes to the host.</summary>
[HarmonyPatch(typeof(WorldSelector), "AutoOrderToTileNow")]
internal static class CoopCaravanGotoPatch
{
    private static bool Prefix(Caravan __0, PlanetTile __1)
    {
        if (!CoopGuest.Active || CoopGuest.Applying)
            return true;
        if (!CoopGuest.Visiting)
            CoopGlobe.SendGoto(__0, __1);
        return false;
    }
}

/// <summary>Guest: the globe's right-click options remember their caravan and tile (picked ones go to the host).</summary>
[HarmonyPatch(typeof(FloatMenuMakerWorld), nameof(FloatMenuMakerWorld.ChoicesAtFor), typeof(PlanetTile), typeof(Caravan))]
internal static class CoopWorldFloatMenuPatch
{
    private static void Postfix(PlanetTile __0, Caravan __1, List<FloatMenuOption> __result)
    {
        if (CoopGuest.Active && !CoopGuest.Applying && __result != null && __1 != null)
        {
            // A visitor's globe is another player's: nothing to order there.
            if (CoopGuest.Visiting)
                __result.Clear();
            else
                CoopGlobe.RememberOptions(__result, __0, __1);
        }
    }
}

/// <summary>Guest: "send" in the form caravan window forms the caravan on the host.</summary>
[HarmonyPatch(typeof(Dialog_FormCaravan), "TryFormAndSendCaravan")]
internal static class CoopFormCaravanPatch
{
    private static bool Prefix(Dialog_FormCaravan __instance, ref bool __result)
    {
        if (!CoopGuest.Active || CoopGuest.Applying)
            return true;
        if (!CoopGuest.Visiting)
            CoopGlobe.SendFormCaravan(__instance);
        __result = !CoopGuest.Visiting;
        return false;
    }
}
