using System;
using System.Collections.Generic;
using System.Linq;
using RimMult.Shared.World;
using RimWorld;
using RimWorld.Planet;
using Verse;

namespace RimMult.Sync;

/// <summary>
/// The NPC settlements of the shared world sit in the same places for everyone. The planet itself is generated alike
/// from the seed, but where the factions' settlements end up also depends on mods' settings, which aren't compared; so
/// the world's creator sends its layout and every game entering the world moves its NPC settlements to match: same
/// tiles, same factions (by def, and which one of that def), same names. Relations, traders and quests stay each
/// colony's own.
/// </summary>
internal static class NpcLayoutSync
{
    /// <summary>This game's NPC settlements, as the world's layout.</summary>
    public static List<NpcSettlement> Capture()
    {
        var factions = FactionsByDef();
        var layout = new List<NpcSettlement>();
        foreach (var settlement in Find.WorldObjects.Settlements)
        {
            if (settlement.Faction is not { IsPlayer: false } faction)
                continue;
            layout.Add(new NpcSettlement
            {
                Tile = settlement.Tile.ToString(),
                Def = settlement.def.defName,
                FactionDef = faction.def.defName,
                FactionIndex = Math.Max(0, factions.TryGetValue(faction.def.defName, out var same) ? same.IndexOf(faction) : 0),
                Name = settlement.Name ?? "",
            });
        }
        return layout;
    }

    /// <summary>Moves this game's NPC settlements to the layout (settlements razed by a player stay gone).</summary>
    public static void Apply(List<NpcSettlement> layout, IReadOnlyList<string> destroyed)
    {
        var factions = FactionsByDef();
        var razed = new HashSet<string>(destroyed);
        var wanted = new Dictionary<string, NpcSettlement>();
        foreach (var entry in layout)
        {
            if (!razed.Contains(entry.Tile) && !wanted.ContainsKey(entry.Tile))
                wanted[entry.Tile] = entry;
        }

        int removed = 0, added = 0, renamed = 0;
        SharedSettlements.WhileApplying(() =>
        {
            foreach (var settlement in Find.WorldObjects.Settlements.ToList())
            {
                // Player colonies and anything a player is on right now (a fight, a visit) are left alone.
                if (settlement.Faction is not { IsPlayer: false } || settlement.HasMap)
                    continue;
                var tile = settlement.Tile.ToString();
                if (wanted.TryGetValue(tile, out var entry) && Matches(settlement, entry, factions))
                {
                    wanted.Remove(tile);
                    if (entry.Name.Length > 0 && settlement.Name != entry.Name)
                    {
                        settlement.Name = entry.Name;
                        renamed++;
                    }
                    continue;
                }
                Find.WorldObjects.Remove(settlement);
                removed++;
            }

            foreach (var entry in wanted.Values)
            {
                if (!PlanetTile.TryParse(entry.Tile, out var tile) || !tile.Valid || Find.WorldObjects.AnyWorldObjectAt(tile))
                    continue; // a colony (or something else) stands there in this game
                if (FactionFor(entry, factions) is not { } faction)
                    continue;
                var def = DefDatabase<WorldObjectDef>.GetNamedSilentFail(entry.Def) ?? WorldObjectDefOf.Settlement;
                if (WorldObjectMaker.MakeWorldObject(def) is not Settlement settlement)
                    continue;
                settlement.SetFaction(faction);
                settlement.Tile = tile;
                settlement.Name = entry.Name.Length > 0 ? entry.Name : faction.Name;
                Find.WorldObjects.Add(settlement);
                added++;
            }
        });

        Log.Message($"[RimMult] NPC settlements matched to the shared world: {layout.Count} in the layout, -{removed} +{added}, renamed {renamed}");
        if (removed + added > 0)
            Messages.Message("RimMult.NpcLayoutApplied".Translate(added, removed), MessageTypeDefOf.NeutralEvent, historical: false);
    }

    /// <summary>The same settlement: the same kind, for the same faction (def, and which one of that def).</summary>
    private static bool Matches(Settlement settlement, NpcSettlement entry, Dictionary<string, List<Faction>> factions) =>
        settlement.def.defName == entry.Def
        && settlement.Faction.def.defName == entry.FactionDef
        && FactionFor(entry, factions) == settlement.Faction;

    private static Faction? FactionFor(NpcSettlement entry, Dictionary<string, List<Faction>> factions)
    {
        if (!factions.TryGetValue(entry.FactionDef, out var same) || same.Count == 0)
            return null;
        return entry.FactionIndex < same.Count ? same[entry.FactionIndex] : same[0];
    }

    /// <summary>NPC factions by def, in creation order (the same order in every game made from the same world).</summary>
    private static Dictionary<string, List<Faction>> FactionsByDef() =>
        Find.FactionManager.AllFactionsListForReading
            .Where(f => !f.IsPlayer)
            .GroupBy(f => f.def.defName)
            .ToDictionary(g => g.Key, g => g.OrderBy(f => f.loadID).ToList());
}
