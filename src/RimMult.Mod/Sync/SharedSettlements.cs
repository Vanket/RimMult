using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using RimMult.ClientCore;
using RimWorld.Planet;
using Verse;

namespace RimMult.Sync;

/// <summary>
/// The NPC part of the shared planet: a settlement destroyed by one player is gone from everyone's globe.
/// (Goodwill, quests and traders stay per colony: each colony has its own relations with the factions.)
/// </summary>
internal static class SharedSettlements
{
    private static RimWorld.Planet.World? _world;
    private static IReadOnlyList<string>? _applied;

    /// <summary>True while we remove settlements ourselves, so that isn't reported back as a new destruction.</summary>
    public static bool Applying { get; private set; }

    public static void Apply(ClientSession session)
    {
        var world = Find.World;
        var destroyed = session.DestroyedSettlements;
        if (world == null || (world == _world && ReferenceEquals(destroyed, _applied)))
            return;
        _world = world;
        _applied = destroyed;

        var tiles = new HashSet<string>(destroyed);
        Applying = true;
        try
        {
            foreach (var settlement in Find.WorldObjects.Settlements.ToList())
            {
                // Leave alone anything a player is on right now (their own fight in progress) and player colonies.
                if (settlement.HasMap || settlement.Faction == null || settlement.Faction.IsPlayer)
                    continue;
                if (tiles.Contains(settlement.Tile.ToString()))
                    Find.WorldObjects.Remove(settlement);
            }
        }
        finally
        {
            Applying = false;
        }
    }

    public static void Reset()
    {
        _world = null;
        _applied = null;
    }
}

/// <summary>
/// A settlement the player defeated (its map cleared of defenders) is the only kind shared with everyone: NPC
/// settlements that mods' wars wipe out or move (Rim War, Dynamic Diplomacy, …) stay in that player's world only.
/// </summary>
[HarmonyPatch(typeof(SettlementDefeatUtility), nameof(SettlementDefeatUtility.CheckDefeated))]
internal static class SettlementDefeatPatch
{
    public static bool Defeating { get; private set; }

    private static void Prefix() => Defeating = true;

    private static void Finalizer() => Defeating = false;
}

[HarmonyPatch(typeof(Settlement), nameof(Settlement.PostRemove))]
internal static class SettlementRemovedPatch
{
    private static void Postfix(Settlement __instance)
    {
        if (SharedSettlements.Applying || !WorldSync.InWorld || !SettlementDefeatPatch.Defeating)
            return;
        if (__instance.Faction == null || __instance.Faction.IsPlayer)
            return;
        Multiplayer.Session?.ReportSettlementDestroyed(__instance.Tile.ToString());
    }
}
