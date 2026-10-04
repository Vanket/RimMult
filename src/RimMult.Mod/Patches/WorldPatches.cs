using System.Collections.Generic;
using System.Text;
using HarmonyLib;
using RimMult.Sync;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace RimMult.Patches;

/// <summary>
/// "Create colony" in an existing shared world: the world-parameters page is locked to the server's planet,
/// so the player generates exactly the same world as everyone else.
/// </summary>
[HarmonyPatch(typeof(Page_CreateWorldParams))]
internal static class CreateWorldParamsPatch
{
    private static readonly AccessTools.FieldRef<Page_CreateWorldParams, string> SeedString = AccessTools.FieldRefAccess<Page_CreateWorldParams, string>("seedString");
    private static readonly AccessTools.FieldRef<Page_CreateWorldParams, float> PlanetCoverage = AccessTools.FieldRefAccess<Page_CreateWorldParams, float>("planetCoverage");
    private static readonly AccessTools.FieldRef<Page_CreateWorldParams, OverallRainfall> Rainfall = AccessTools.FieldRefAccess<Page_CreateWorldParams, OverallRainfall>("rainfall");
    private static readonly AccessTools.FieldRef<Page_CreateWorldParams, OverallTemperature> Temperature = AccessTools.FieldRefAccess<Page_CreateWorldParams, OverallTemperature>("temperature");
    private static readonly AccessTools.FieldRef<Page_CreateWorldParams, OverallPopulation> Population = AccessTools.FieldRefAccess<Page_CreateWorldParams, OverallPopulation>("population");
    private static readonly AccessTools.FieldRef<Page_CreateWorldParams, LandmarkDensity> Landmarks = AccessTools.FieldRefAccess<Page_CreateWorldParams, LandmarkDensity>("landmarkDensity");
    private static readonly AccessTools.FieldRef<Page_CreateWorldParams, List<FactionDef>> Factions = AccessTools.FieldRefAccess<Page_CreateWorldParams, List<FactionDef>>("factions");

    /// <summary>Re-applied every frame and right before generating, so edits in the page have no effect.</summary>
    [HarmonyPrefix]
    [HarmonyPatch(nameof(Page_CreateWorldParams.DoWindowContents))]
    private static void BeforeDraw(Page_CreateWorldParams __instance) => Apply(__instance);

    [HarmonyPrefix]
    [HarmonyPatch("CanDoNext")]
    private static void BeforeNext(Page_CreateWorldParams __instance) => Apply(__instance);

    [HarmonyPostfix]
    [HarmonyPatch(nameof(Page_CreateWorldParams.DoWindowContents))]
    private static void AfterDraw(Rect rect)
    {
        if (SharedWorld() == null)
            return;
        var note = new Rect(rect.xMax - 460f, rect.y, 460f, 50f);
        GUI.color = ColorLibrary.SkyBlue;
        Widgets.Label(note, "RimMult.WorldLocked".Translate());
        GUI.color = Color.white;
    }

    private static Shared.World.WorldDefinition? SharedWorld() =>
        WorldSync.NewColonyFlowActive ? Multiplayer.Session?.World : null;

    private static void Apply(Page_CreateWorldParams page)
    {
        if (SharedWorld() is not { } world)
            return;

        SeedString(page) = world.SeedString;
        PlanetCoverage(page) = world.PlanetCoverage;
        Rainfall(page) = WorldDefinitions.Rainfall(world);
        Temperature(page) = WorldDefinitions.Temperature(world);
        Population(page) = WorldDefinitions.Population(world);
        Landmarks(page) = WorldDefinitions.Landmarks(world);
        page.pollution = world.Pollution;
        if (WorldDefinitions.ResolveFactions(world) is { } factions)
            Factions(page) = factions;
    }
}

/// <summary>Nobody may found a colony on top of (or right next to) another player's colony.</summary>
[HarmonyPatch(typeof(TileFinder), nameof(TileFinder.IsValidTileForNewSettlement))]
internal static class SettlementTilePatch
{
    private static void Postfix(PlanetTile tile, StringBuilder reason, ref bool __result)
    {
        if (!__result || !(WorldSync.InWorld || WorldSync.NewColonyFlowActive))
            return;
        if (!RemoteColonies.IsNearOtherPlayer(tile))
            return;

        __result = false;
        reason?.Append("RimMult.TileNearPlayer".Translate());
    }
}

[HarmonyPatch(typeof(TickManager))]
internal static class TimePatches
{
    /// <summary>A colony that is ahead of the shared clock waits for the others.</summary>
    [HarmonyPostfix]
    [HarmonyPatch(nameof(TickManager.Paused), MethodType.Getter)]
    private static void PausedAtHorizon(ref bool __result)
    {
        if (!__result && TimeSync.BlockedByHorizon)
            __result = true;
    }

    /// <summary>
    /// In the shared world RimWorld's own Superfast doubling ("nothing happening") only applies when the server grants
    /// it — when nothing happens in anyone's game — and the game eases off while it is ahead of the others.
    /// </summary>
    [HarmonyPostfix]
    [HarmonyPatch(nameof(TickManager.TickRateMultiplier), MethodType.Getter)]
    private static void SharedRate(TickManager __instance, ref float __result)
    {
        if (!TimeSync.Active)
            return;
        if (__instance.CurTimeSpeed == TimeSpeed.Superfast && !TimeSync.Boost && __result > 6f)
            __result = 6f;
        __result *= TimeSync.SpeedFactor;
    }

    /// <summary>
    /// The game's own automatic pauses (letters, events) would pause the whole world for everyone through the
    /// speed vote; in a shared world only players pause.
    /// </summary>
    [HarmonyPrefix]
    [HarmonyPatch(nameof(TickManager.Pause))]
    private static bool NoAutoPause() => !TimeSync.Active && !Coop.CoopGuest.Active;
}

/// <summary>
/// Open dialogs normally freeze the game. In a shared world that would freeze everyone (the clock waits for the
/// slowest colony), so time keeps running behind windows, like in other multiplayer mods.
/// </summary>
[HarmonyPatch(typeof(WindowStack), nameof(WindowStack.WindowsForcePause), MethodType.Getter)]
internal static class WindowsForcePausePatch
{
    private static void Postfix(ref bool __result)
    {
        if (__result && (TimeSync.Active || Coop.CoopGuest.Active))
            __result = false;
    }
}
