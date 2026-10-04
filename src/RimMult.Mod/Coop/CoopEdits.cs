using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text;
using HarmonyLib;
using RimMult.Shared.Coop;
using RimWorld;
using UnityEngine;
using Verse;

namespace RimMult.Coop;

/// <summary>
/// Settings a co-op guest changes in its own copy: bills, storage settings, what a grower plants, who owns a bed, a
/// zone's settings, a pawn's schedule/policies/area/medicine, the policies themselves and the allowed areas. The
/// guest's menus work on its copy as usual; every change is noticed (by comparing with what the host last sent)
/// and goes to the host as the saved state of that part, which the host takes over and streams back to everyone.
/// </summary>
internal static class CoopEdits
{
    private const float CheckInterval = 0.25f;

    private static readonly AccessTools.FieldRef<BillStack, List<Bill>> BillsOf = AccessTools.FieldRefAccess<BillStack, List<Bill>>("bills");
    private static readonly AccessTools.FieldRef<Bill, int> BillLoadId = AccessTools.FieldRefAccess<Bill, int>("loadID");
    private static readonly AccessTools.FieldRef<AreaManager, List<Area>> AreasOf = AccessTools.FieldRefAccess<AreaManager, List<Area>>("areas");
    private static readonly AccessTools.FieldRef<Area, BoolGrid> InnerGrid = AccessTools.FieldRefAccess<Area, BoolGrid>("innerGrid");
    private static readonly MethodInfo AreaDrawer = AccessTools.PropertyGetter(typeof(Area), "Drawer");
    private static readonly MethodInfo SortAreas = AccessTools.Method(typeof(AreaManager), "SortAreas");

    /// <summary>Bill fields that are the host's progress, not the player's settings.</summary>
    private static readonly HashSet<string> BillProgressFields = new() { "billStack", "loadID", "nextTickToSearchForIngredients", "boundUftInt", "deleted" };

    // ---------- guest: noticing changes ----------

    /// <summary>What each watched part looked like when the host last sent it (or this guest last sent it).</summary>
    private static readonly Dictionary<string, string> Baseline = new();

    private static float _lastCheck;
    private static bool _policiesWindowWasOpen;
    private static bool _areasWindowWasOpen;

    public static void Reset()
    {
        Baseline.Clear();
        GuestBills.Clear();
        GuestAreas.Clear();
        _policiesWindowWasOpen = false;
        _areasWindowWasOpen = false;
    }

    /// <summary>The host's fresh copy of a thing (or pawn) arrived: what it carries is the new starting point.</summary>
    public static void ForgetThing(int thingId)
    {
        Baseline.Remove("p:" + thingId);
        foreach (var part in ThingParts)
            Baseline.Remove($"t:{thingId}:{part}");
    }

    public static void ForgetZones(int mapId)
    {
        foreach (var key in Baseline.Keys.Where(k => k.StartsWith($"z:{mapId}:", StringComparison.Ordinal)).ToList())
            Baseline.Remove(key);
    }

    public static void ForgetAreas(int mapId) => Baseline.Remove("a:" + mapId);

    public static void ForgetPolicies() => Baseline.Remove("policies");

    private static readonly string[] ThingParts = { "bills", "storage", "plant", "owners" };

    /// <summary>Compares the watched parts with how they were; changed ones go to the host. Cheap; throttled unless <paramref name="force"/>.</summary>
    public static void GuestUpdate(bool force)
    {
        var now = Time.realtimeSinceStartup;
        if ((!force && now - _lastCheck < CheckInterval) || !ScribeMemory.Idle)
            return;
        _lastCheck = now;
        try
        {
            CheckThings();
            CheckZone();
            CheckPawns();
            CheckPolicies();
            CheckAreas();
        }
        catch (Exception e)
        {
            Log.ErrorOnce($"[RimMult] Co-op: could not look for settings changes: {e}", 0x434F4544);
        }
    }

    private static bool Changed(string key, string current)
    {
        if (!Baseline.TryGetValue(key, out var old))
        {
            Baseline[key] = current;
            return false;
        }
        if (old == current)
            return false;
        Baseline[key] = current;
        return true;
    }

    private static void CheckThings()
    {
        var watched = new HashSet<Thing>(Find.Selector.SelectedObjects.OfType<Thing>().Where(t => t is not Pawn && t.Spawned));
        foreach (var window in Find.WindowStack.Windows)
            foreach (var thing in ThingsEditedBy(window))
                if (thing.Spawned)
                    watched.Add(thing);

        foreach (var thing in watched)
        {
            if (thing.thingIDNumber < 0)
                continue;
            foreach (var part in ThingParts)
            {
                var current = SaveThingPart(thing, part);
                if (current != null && Changed($"t:{thing.thingIDNumber}:{part}", current))
                    Send(part, thing.Map, thing.thingIDNumber, current);
            }
        }
    }

    private static void CheckZone()
    {
        if (Find.Selector.SelectedZone is not { } zone || zone.Map == null)
            return;
        var current = SaveZone(zone);
        if (Changed($"z:{zone.Map.uniqueID}:{zone.ID}", current))
            Multiplayer.Session?.SendCoop(CoopChannel.Command, new CoopCommand
            {
                Kind = CoopCommandKind.Edit, MapId = zone.Map.uniqueID, Name = "zone", Number = zone.ID, Extra = current,
            }.Encode());
    }

    private static void CheckPawns()
    {
        foreach (var map in Find.Maps)
        {
            foreach (var pawn in map.mapPawns.SpawnedPawnsInFaction(Faction.OfPlayer))
            {
                var settings = PawnSettings(pawn);
                var key = "p:" + pawn.thingIDNumber;
                var had = Baseline.TryGetValue(key, out var oldText);
                if (!Changed(key, Join(settings)) || !had)
                    continue;
                // Only what this guest changed: the rest may have changed on the host meanwhile.
                var old = Split(oldText!);
                var changed = settings.Where(p => !old.TryGetValue(p.Key, out var v) || v != p.Value).ToDictionary(p => p.Key, p => p.Value);
                if (changed.Count > 0)
                    Send("pawn", map, pawn.thingIDNumber, Join(changed));
            }
        }
    }

    private static void CheckPolicies()
    {
        var open = Find.WindowStack.Windows.Any(w => w.GetType().Name.IndexOf("Polic", StringComparison.Ordinal) >= 0);
        if (!open && !_policiesWindowWasOpen)
            return;
        _policiesWindowWasOpen = open;
        var current = SavePolicies();
        if (Changed("policies", current))
            Send("policies", null, -1, current);
    }

    private static void CheckAreas()
    {
        var open = Find.WindowStack.Windows.Any(w => w.GetType().Name.IndexOf("Area", StringComparison.Ordinal) >= 0);
        if (!open && !_areasWindowWasOpen)
            return;
        _areasWindowWasOpen = open;
        if (Find.CurrentMap is not { } map)
            return;

        var summary = AreaSummary(map);
        var key = "a:" + map.uniqueID;
        var had = Baseline.TryGetValue(key, out var oldText);
        if (!Changed(key, Join(summary)) || !had)
            return;
        var old = Split(oldText!);
        var changed = summary.Where(p => old.TryGetValue(p.Key, out var v) && v.Split('|').Last() != p.Value.Split('|').Last()).Select(p => p.Key);
        var added = summary.Keys.Where(k => !old.ContainsKey(k));
        var removed = old.Keys.Where(k => !summary.ContainsKey(k));
        Multiplayer.Session?.SendCoop(CoopChannel.Command, new CoopCommand
        {
            Kind = CoopCommandKind.Edit,
            MapId = map.uniqueID,
            Name = "areas",
            Detail = $"{string.Join(",", changed)};{string.Join(",", added)};{string.Join(",", removed)}",
            Extra = ScribeMemory.Save(map.areaManager.ExposeData),
        }.Encode());
    }

    /// <summary>Allowed areas by id: label, color and a hash of their cells.</summary>
    private static Dictionary<string, string> AreaSummary(Map map)
    {
        var summary = new Dictionary<string, string>();
        var cells = map.cellIndices.NumGridCells;
        foreach (var area in map.areaManager.AllAreas.OfType<Area_Allowed>())
        {
            var hash = 17;
            for (var i = 0; i < cells; i++)
                if (area[i])
                    hash = unchecked(hash * 31 + i);
            summary[area.ID.ToString(CultureInfo.InvariantCulture)] = $"{area.Label}|{ColorUtility.ToHtmlStringRGBA(area.Color)}|{hash}";
        }
        return summary;
    }

    private static void Send(string part, Map? map, int thingId, string value)
    {
        var command = new CoopCommand { Kind = CoopCommandKind.Edit, MapId = map?.uniqueID ?? -1, Name = part, Extra = value };
        if (thingId >= 0)
            command.ThingIds.Add(thingId);
        Multiplayer.Session?.SendCoop(CoopChannel.Command, command.Encode());
    }

    // ---------- which things open windows edit ----------

    private static readonly Dictionary<Type, FieldInfo[]> WindowFields = new();

    /// <summary>
    /// The things an open window works on (a bill's workbench, a bed whose owner is being picked, …), found by its
    /// fields: while it is open, the host's fresh copy of those things waits, or the window would edit a stale one.
    /// </summary>
    public static IEnumerable<Thing> ThingsEditedBy(Window window)
    {
        var type = window.GetType();
        if (!WindowFields.TryGetValue(type, out var fields))
        {
            fields = AccessTools.GetDeclaredFields(type)
                .Concat(type.BaseType != null && type.BaseType != typeof(Window) ? AccessTools.GetDeclaredFields(type.BaseType) : Enumerable.Empty<FieldInfo>())
                .Where(f => !f.IsStatic && (typeof(Thing).IsAssignableFrom(f.FieldType) || typeof(ThingComp).IsAssignableFrom(f.FieldType)
                                            || typeof(Bill).IsAssignableFrom(f.FieldType) || f.FieldType == typeof(IBillGiver)))
                .ToArray();
            WindowFields[type] = fields;
        }
        foreach (var field in fields)
        {
            var value = field.GetValue(window);
            var thing = value switch
            {
                Thing t => t,
                ThingComp c => c.parent,
                Bill b => b.billStack?.billGiver as Thing,
                IBillGiver g => g as Thing,
                _ => null,
            };
            if (thing != null)
                yield return thing;
        }
    }

    /// <summary>Whether an open window is editing this thing right now.</summary>
    public static bool IsEditing(Thing thing)
    {
        foreach (var window in Find.WindowStack.Windows)
            foreach (var edited in ThingsEditedBy(window))
                if (edited == thing || edited.thingIDNumber == thing.thingIDNumber)
                    return true;
        return false;
    }

    // ---------- the parts, saved ----------

    private static string? SaveThingPart(Thing thing, string part)
    {
        switch (part)
        {
            case "bills" when thing is IBillGiver { BillStack: { } stack }:
                var bills = stack.Bills.ToList();
                return ScribeMemory.Save(() => Scribe_Collections.Look(ref bills, "bills", LookMode.Deep));
            case "storage" when StoreSettingsOf(thing) is { } settings:
                return ScribeMemory.Save(settings.ExposeData);
            case "plant" when thing is IPlantToGrowSettable grower:
                return grower.GetPlantDefToGrow()?.defName ?? "";
            case "owners" when thing.TryGetComp<CompAssignableToPawn>() is { } assignable:
                return string.Join(",", assignable.AssignedPawnsForReading.Select(p => p.thingIDNumber));
            default:
                return null;
        }
    }

    private static StorageSettings? StoreSettingsOf(Thing thing)
    {
        if (thing is IStoreSettingsParent parent)
            return parent.GetStoreSettings();
        if (thing is ThingWithComps withComps)
            foreach (var comp in withComps.AllComps)
                if (comp is IStoreSettingsParent compParent && compParent.GetStoreSettings() is { } settings)
                    return settings;
        return null;
    }

    private static string SaveZone(Zone zone)
    {
        var text = new StringBuilder(zone.label ?? "").Append('\n');
        if (zone is Zone_Growing growing)
            text.Append(growing.GetPlantDefToGrow()?.defName ?? "").Append('|').Append(growing.allowSow).Append('|').Append(growing.allowCut);
        else if (zone is Zone_Stockpile stockpile && stockpile.settings != null)
            text.Append(ScribeMemory.Save(stockpile.settings.ExposeData));
        return text.ToString();
    }

    private static Dictionary<string, string> PawnSettings(Pawn pawn)
    {
        var settings = new Dictionary<string, string>();
        if (pawn.timetable?.times is { } times)
            settings["times"] = string.Join(",", times.Select(t => t?.defName ?? ""));
        if (pawn.outfits?.CurrentApparelPolicy is { } outfit)
            settings["outfit"] = outfit.id.ToString(CultureInfo.InvariantCulture);
        if (pawn.drugs?.CurrentPolicy is { } drugs)
            settings["drugs"] = drugs.id.ToString(CultureInfo.InvariantCulture);
        if (pawn.foodRestriction?.CurrentFoodPolicy is { } food)
            settings["food"] = food.id.ToString(CultureInfo.InvariantCulture);
        if (pawn.playerSettings is { } player)
        {
            if (player.SupportsAllowedAreas)
                settings["area"] = (player.AreaRestrictionInPawnCurrentMap?.ID ?? -1).ToString(CultureInfo.InvariantCulture);
            settings["med"] = ((int)player.medCare).ToString(CultureInfo.InvariantCulture);
            settings["hostility"] = ((int)player.hostilityResponse).ToString(CultureInfo.InvariantCulture);
            settings["selfTend"] = player.selfTend.ToString();
            settings["followDrafted"] = player.followDrafted.ToString();
            settings["followFieldwork"] = player.followFieldwork.ToString();
            settings["master"] = (player.Master?.thingIDNumber ?? -1).ToString(CultureInfo.InvariantCulture);
        }
        return settings;
    }

    private static string Join(Dictionary<string, string> values) =>
        string.Join("\n", values.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => p.Key + "=" + p.Value.Replace('\n', ' ')));

    private static Dictionary<string, string> Split(string text)
    {
        var values = new Dictionary<string, string>();
        foreach (var line in text.Split('\n'))
        {
            var eq = line.IndexOf('=');
            if (eq > 0)
                values[line.Substring(0, eq)] = line.Substring(eq + 1);
        }
        return values;
    }

    public static string SavePolicies()
    {
        var game = Current.Game;
        var outfits = game.outfitDatabase;
        var drugs = game.drugPolicyDatabase;
        var food = game.foodRestrictionDatabase;
        return ScribeMemory.Save(() =>
        {
            Scribe_Deep.Look(ref outfits, "outfits");
            Scribe_Deep.Look(ref drugs, "drugs");
            Scribe_Deep.Look(ref food, "food");
        });
    }

    // ---------- host: taking a guest's edit over ----------

    /// <summary>Bills a guest made (with an id from its stale copy) → the host's bill made from them.</summary>
    private static readonly Dictionary<string, Bill> GuestBills = new();

    /// <summary>Allowed areas a guest made: (map, guest's area id) → the host's area id.</summary>
    private static readonly Dictionary<(int Map, int Area), int> GuestAreas = new();

    public static void Apply(CoopCommand command, Map? map)
    {
        Thing? thing = null;
        if (command.ThingIds.Count > 0)
            thing = Find.Maps.Select(m => CoopCommands.ThingsById(m).TryGetValue(command.ThingIds[0], out var t) ? t : null).FirstOrDefault(t => t != null);

        switch (command.Name)
        {
            case "bills" when thing is IBillGiver { BillStack: { } stack }:
                ApplyBills(stack, command.Extra);
                break;
            case "storage" when thing != null && StoreSettingsOf(thing) is { } settings:
                ApplyStorage(settings, command.Extra);
                break;
            case "plant" when thing is IPlantToGrowSettable grower:
                grower.SetPlantDefToGrow(DefDatabase<ThingDef>.GetNamedSilentFail(command.Extra));
                break;
            case "owners" when thing?.TryGetComp<CompAssignableToPawn>() is { } assignable:
                ApplyOwners(assignable, command.Extra);
                break;
            case "zone" when map != null && map.zoneManager.AllZones.FirstOrDefault(z => z.ID == command.Number) is { } zone:
                ApplyZone(zone, command.Extra);
                CoopHost.TouchZones(map);
                break;
            case "pawn" when thing is Pawn pawn:
                ApplyPawn(pawn, Split(command.Extra));
                break;
            case "policies":
                ApplyPolicies(command.Extra, fromGuest: true);
                break;
            case "areas" when map != null:
                ApplyGuestAreas(map, command.Extra, command.Detail);
                break;
            default:
                return;
        }
        if (thing != null)
            CoopHost.Touch(thing);
    }

    private static void ApplyBills(BillStack stack, string xml)
    {
        List<Bill>? loaded = null;
        ScribeMemory.Load(ScribeMemory.Parse(xml), new HashSet<string>(), () => Scribe_Collections.Look(ref loaded, "bills", LookMode.Deep), () =>
        {
            if (loaded != null)
                foreach (var bill in loaded.Where(b => b != null))
                    bill.billStack = stack;
        });
        if (loaded == null)
            return;

        var existing = stack.Bills.ToDictionary(b => b.GetUniqueLoadID());
        var result = new List<Bill>();
        foreach (var bill in loaded.Where(b => b != null))
        {
            var guestId = bill.GetUniqueLoadID();
            var id = GuestBills.TryGetValue(guestId, out var made) ? made.GetUniqueLoadID() : guestId;
            if (existing.TryGetValue(id, out var current) && current.GetType() == bill.GetType())
            {
                CopyFields(bill, current, typeof(Bill), BillProgressFields);
                existing.Remove(id);
                result.Add(current);
            }
            else
            {
                // New on the guest's side: a fresh id from the host's counter (the guest's one is stale).
                BillLoadId(bill) = Find.UniqueIDsManager.GetNextBillID();
                bill.billStack = stack;
                GuestBills[guestId] = bill;
                result.Add(bill);
            }
        }
        foreach (var gone in existing.Values)
            stack.Delete(gone);
        var bills = BillsOf(stack);
        bills.Clear();
        bills.AddRange(result);
    }

    /// <summary>Copies every instance field declared from <paramref name="from"/>'s type down to <paramref name="upTo"/>.</summary>
    private static void CopyFields(object from, object to, Type upTo, ISet<string> skip)
    {
        for (var type = from.GetType(); type != null && type != typeof(object); type = type.BaseType)
        {
            foreach (var field in AccessTools.GetDeclaredFields(type))
                if (!field.IsStatic && !field.IsInitOnly && !skip.Contains(field.Name))
                    field.SetValue(to, field.GetValue(from));
            if (type == upTo)
                break;
        }
    }

    private static void ApplyStorage(StorageSettings settings, string xml)
    {
        var loaded = new StorageSettings(settings.owner);
        ScribeMemory.Load(ScribeMemory.Parse(xml), new HashSet<string>(), loaded.ExposeData);
        settings.CopyFrom(loaded);
    }

    private static void ApplyOwners(CompAssignableToPawn assignable, string ids)
    {
        var wanted = ids.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(s => int.TryParse(s, out var id) ? id : -1).Where(id => id >= 0).ToList();
        foreach (var pawn in assignable.AssignedPawnsForReading.ToList())
            if (!wanted.Contains(pawn.thingIDNumber))
                assignable.TryUnassignPawn(pawn);
        foreach (var id in wanted)
        {
            if (assignable.AssignedPawnsForReading.Any(p => p.thingIDNumber == id))
                continue;
            if (FindPawn(id) is { } pawn && assignable.CanAssignTo(pawn).Accepted)
                assignable.TryAssignPawn(pawn);
        }
    }

    private static Pawn? FindPawn(int id)
    {
        foreach (var map in Find.Maps)
            foreach (var pawn in map.mapPawns.AllPawns)
                if (pawn.thingIDNumber == id)
                    return pawn;
        return null;
    }

    private static void ApplyZone(Zone zone, string text)
    {
        var newline = text.IndexOf('\n');
        if (newline < 0)
            return;
        var label = text.Substring(0, newline);
        var rest = text.Substring(newline + 1);
        if (label.Length > 0 && label != zone.label)
            zone.label = label;
        if (zone is Zone_Growing growing)
        {
            var parts = rest.Split('|');
            if (parts.Length == 3)
            {
                var plant = DefDatabase<ThingDef>.GetNamedSilentFail(parts[0]);
                if (plant != null && plant != growing.GetPlantDefToGrow())
                    growing.SetPlantDefToGrow(plant);
                growing.allowSow = parts[1] == bool.TrueString;
                growing.allowCut = parts[2] == bool.TrueString;
            }
        }
        else if (zone is Zone_Stockpile { settings: { } settings } && rest.Length > 0)
        {
            ApplyStorage(settings, rest);
        }
    }

    private static void ApplyPawn(Pawn pawn, Dictionary<string, string> settings)
    {
        var game = Current.Game;
        foreach (var pair in settings)
        {
            var key = pair.Key;
            var value = pair.Value;
            int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number);
            switch (key)
            {
                case "times" when pawn.timetable != null:
                    var hours = value.Split(',');
                    for (var hour = 0; hour < hours.Length && hour < 24; hour++)
                        if (DefDatabase<TimeAssignmentDef>.GetNamedSilentFail(hours[hour]) is { } assignment && pawn.timetable.GetAssignment(hour) != assignment)
                            pawn.timetable.SetAssignment(hour, assignment);
                    break;
                case "outfit" when pawn.outfits != null && game.outfitDatabase.AllOutfits.FirstOrDefault(p => p.id == number) is { } outfit:
                    pawn.outfits.CurrentApparelPolicy = outfit;
                    break;
                case "drugs" when pawn.drugs != null && game.drugPolicyDatabase.AllPolicies.FirstOrDefault(p => p.id == number) is { } drugs:
                    pawn.drugs.CurrentPolicy = drugs;
                    break;
                case "food" when pawn.foodRestriction != null && game.foodRestrictionDatabase.AllFoodRestrictions.FirstOrDefault(p => p.id == number) is { } food:
                    pawn.foodRestriction.CurrentFoodPolicy = food;
                    break;
                case "area" when pawn.playerSettings != null && pawn.MapHeld is { } map:
                    pawn.playerSettings.AreaRestrictionInPawnCurrentMap = HostArea(map, number);
                    break;
                case "med" when pawn.playerSettings != null:
                    pawn.playerSettings.medCare = (MedicalCareCategory)number;
                    break;
                case "hostility" when pawn.playerSettings != null:
                    pawn.playerSettings.hostilityResponse = (HostilityResponseMode)number;
                    break;
                case "selfTend" when pawn.playerSettings != null:
                    pawn.playerSettings.selfTend = value == bool.TrueString;
                    break;
                case "followDrafted" when pawn.playerSettings != null:
                    pawn.playerSettings.followDrafted = value == bool.TrueString;
                    break;
                case "followFieldwork" when pawn.playerSettings != null:
                    pawn.playerSettings.followFieldwork = value == bool.TrueString;
                    break;
                case "master" when pawn.playerSettings != null:
                    pawn.playerSettings.Master = number >= 0 ? FindPawn(number) : null;
                    break;
            }
        }
    }

    /// <summary>The host's area a guest means by <paramref name="id"/> (an area the guest made maps to the host's copy).</summary>
    public static Area? HostArea(Map map, int id)
    {
        if (id < 0)
            return null;
        if (GuestAreas.TryGetValue((map.uniqueID, id), out var hostId))
            id = hostId;
        return map.areaManager.AllAreas.FirstOrDefault(a => a.ID == id);
    }

    private static void ApplyGuestAreas(Map map, string xml, string detail)
    {
        var lists = detail.Split(';');
        HashSet<int> Ids(int index) => new(index < lists.Length
            ? lists[index].Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries).Select(s => int.TryParse(s, out var id) ? id : -1)
            : Enumerable.Empty<int>());
        var changed = Ids(0);
        var added = Ids(1);
        var removed = Ids(2);

        var loaded = LoadAreas(map, xml);
        foreach (var area in loaded.OfType<Area_Allowed>())
        {
            var host = HostArea(map, area.ID) as Area_Allowed;
            if (host == null)
            {
                if (!added.Contains(area.ID) || !map.areaManager.TryMakeNewAllowed(out host))
                    continue;
                GuestAreas[(map.uniqueID, area.ID)] = host.ID;
                changed.Add(area.ID);
            }
            if (host.Label != area.Label)
                host.SetLabel(area.Label);
            if (host.Color != area.Color)
                host.SetColor(area.Color);
            if (changed.Contains(area.ID))
                CopyCells(area, host);
        }
        foreach (var id in removed)
            if (HostArea(map, id) is Area_Allowed gone)
                gone.Delete();
    }

    private static List<Area> LoadAreas(Map map, string xml)
    {
        var temp = new AreaManager(map);
        ScribeMemory.Load(ScribeMemory.Parse(xml), new HashSet<string>(map.areaManager.AllAreas.Select(a => a.GetUniqueLoadID())), temp.ExposeData);
        return temp.AllAreas.ToList();
    }

    private static void CopyCells(Area from, Area to)
    {
        var cells = to.Map.cellIndices.NumGridCells;
        for (var i = 0; i < cells; i++)
            if (to[i] != from[i])
                to[i] = from[i];
    }

    // ---------- guest: taking the host's state over ----------

    /// <summary>Guest: the host's areas, merged into the copy's (same objects kept, so pawns' restrictions stay valid).</summary>
    public static void ApplyHostAreas(Map map, string xml)
    {
        var loaded = LoadAreas(map, xml);
        var manager = map.areaManager;
        var list = AreasOf(manager);
        var byId = list.ToDictionary(a => a.ID);
        foreach (var area in loaded)
        {
            if (byId.TryGetValue(area.ID, out var current) && current.GetType() == area.GetType())
            {
                byId.Remove(area.ID);
                InnerGrid(current) = InnerGrid(area);
                if (current is Area_Allowed allowed && area is Area_Allowed hostAllowed)
                {
                    if (allowed.Label != hostAllowed.Label)
                        allowed.SetLabel(hostAllowed.Label);
                    if (allowed.Color != hostAllowed.Color)
                        allowed.SetColor(hostAllowed.Color);
                }
                try
                {
                    (AreaDrawer.Invoke(current, null) as CellBoolDrawer)?.SetDirty();
                }
                catch (Exception)
                {
                    // Redrawn the next time the area changes.
                }
            }
            else
            {
                area.areaManager = manager;
                list.Add(area);
            }
        }
        foreach (var gone in byId.Values)
            gone.Delete();
        SortAreas?.Invoke(manager, null);
        ForgetAreas(map.uniqueID);
    }

    /// <summary>Host or guest: the other side's policies, merged into this game's (same objects kept, pawns keep theirs).</summary>
    public static void ApplyPolicies(string xml, bool fromGuest = false)
    {
        OutfitDatabase? outfits = null;
        DrugPolicyDatabase? drugs = null;
        FoodRestrictionDatabase? food = null;
        var game = Current.Game;
        var replaced = new HashSet<string>(game.outfitDatabase.AllOutfits.Select(p => p.GetUniqueLoadID())
            .Concat(game.drugPolicyDatabase.AllPolicies.Select(p => p.GetUniqueLoadID()))
            .Concat(game.foodRestrictionDatabase.AllFoodRestrictions.Select(p => p.GetUniqueLoadID())));
        ScribeMemory.Load(ScribeMemory.Parse(xml), replaced, () =>
        {
            Scribe_Deep.Look(ref outfits, "outfits");
            Scribe_Deep.Look(ref drugs, "drugs");
            Scribe_Deep.Look(ref food, "food");
        });
        if (outfits != null)
            Merge(game.outfitDatabase.AllOutfits, outfits.AllOutfits, p => game.outfitDatabase.TryDelete(p), fromGuest);
        if (drugs != null)
            Merge(game.drugPolicyDatabase.AllPolicies, drugs.AllPolicies, p => game.drugPolicyDatabase.TryDelete(p), fromGuest);
        if (food != null)
            Merge(game.foodRestrictionDatabase.AllFoodRestrictions, food.AllFoodRestrictions, p => game.foodRestrictionDatabase.TryDelete(p), fromGuest);
        ForgetPolicies();
    }

    private static void Merge<T>(List<T> live, List<T> incoming, Func<T, AcceptanceReport> delete, bool fromGuest) where T : Policy
    {
        // A guest's copy may not have the host's newest policies yet (ids only grow): those are not deletions.
        var newestKnown = incoming.Count > 0 ? incoming.Max(p => p.id) : 0;
        var byId = live.ToDictionary(p => p.id);
        foreach (var policy in incoming)
        {
            if (byId.TryGetValue(policy.id, out var current))
            {
                byId.Remove(policy.id);
                current.CopyFrom(policy);
                current.label = policy.label;
            }
            else
            {
                live.Add(policy);
            }
        }
        foreach (var gone in byId.Values)
            if (!fromGuest || gone.id <= newestKnown)
                delete(gone);
    }
}
