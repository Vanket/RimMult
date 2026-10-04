using System;
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

    /// <summary>Runs a change of NPC settlements made by RimMult itself (not news to report as a destruction).</summary>
    public static void WhileApplying(Action action)
    {
        Applying = true;
        try
        {
            action();
        }
        finally
        {
            Applying = false;
        }
    }
}

/// <summary>
/// Which vanished NPC settlements are shared with everyone. Normally all of them; but mods that wage wars between
/// NPC factions on the globe (Rim War, Dynamic Diplomacy) wipe out and found settlements in each player's world on
/// their own, so with them only the settlements a player defeated (its map cleared of defenders) are shared.
/// </summary>
[HarmonyPatch(typeof(SettlementDefeatUtility), nameof(SettlementDefeatUtility.CheckDefeated))]
internal static class SettlementDefeatPatch
{
    private static bool? _warMods;

    /// <summary>A mod that changes NPC settlements by itself is active.</summary>
    public static bool WarMods => _warMods ??= ModsConfig.ActiveModsInLoadOrder.Any(m =>
        m.Name.IndexOf("Rim War", StringComparison.OrdinalIgnoreCase) >= 0
        || m.Name.IndexOf("RimWar", StringComparison.OrdinalIgnoreCase) >= 0
        || m.Name.IndexOf("Dynamic Diplomacy", StringComparison.OrdinalIgnoreCase) >= 0);

    public static bool Defeating { get; private set; }

    private static void Prefix() => Defeating = true;

    private static void Finalizer() => Defeating = false;
}

[HarmonyPatch(typeof(Settlement), nameof(Settlement.PostRemove))]
internal static class SettlementRemovedPatch
{
    private static void Postfix(Settlement __instance)
    {
        if (SharedSettlements.Applying || !WorldSync.InWorld || (SettlementDefeatPatch.WarMods && !SettlementDefeatPatch.Defeating))
            return;
        if (__instance.Faction == null || __instance.Faction.IsPlayer)
            return;
        Multiplayer.Session?.ReportSettlementDestroyed(__instance.Tile.ToString());
    }
}
