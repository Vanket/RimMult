using System;
using System.Reflection;
using HarmonyLib;
using RimMult.Coop;
using RimMult.Sync;
using RimWorld.Planet;
using Verse;

namespace RimMult.Patches;

/// <summary>
/// The route planners (the game's, and Vehicle Framework's for vehicles) pause the game when they start and stop as
/// soon as it isn't paused. In multiplayer the speed is the shared one, set back every frame, so they closed at once
/// and no route could be planned (no caravan from home could leave in 1.6, where a route is a must). There they
/// neither pause the game nor stop because it runs: the world goes on while one plans.
/// </summary>
internal static class RoutePlanning
{
    /// <summary>The game's speed belongs to the shared world (separate colonies) or the co-op host.</summary>
    public static bool SharedSpeed => Current.ProgramState == ProgramState.Playing && (TimeSync.Active || CoopGuest.Active);

    /// <summary>Remembers the speed before a planner starts…</summary>
    public static TimeSpeed Before() => Find.TickManager?.CurTimeSpeed ?? TimeSpeed.Paused;

    /// <summary>…and puts it back if the planner paused the game (that would be a pause vote for everyone).</summary>
    public static void Restore(TimeSpeed before)
    {
        if (SharedSpeed && Find.TickManager is { } ticks && ticks.CurTimeSpeed != before)
            ticks.CurTimeSpeed = before;
    }

    /// <summary>A planner on the globe keeps going though the game runs.</summary>
    public static bool KeepGoing(bool active) => active && SharedSpeed && WorldRendererUtility.WorldSelected;
}

[HarmonyPatch(typeof(WorldRoutePlanner), nameof(WorldRoutePlanner.Start), typeof(PlanetLayer))]
internal static class RoutePlannerStartPatch
{
    private static void Prefix(out TimeSpeed __state) => __state = RoutePlanning.Before();

    private static void Postfix(TimeSpeed __state) => RoutePlanning.Restore(__state);
}

[HarmonyPatch(typeof(WorldRoutePlanner), "ShouldStop", MethodType.Getter)]
internal static class RoutePlannerKeepPatch
{
    private static void Postfix(WorldRoutePlanner __instance, ref bool __result)
    {
        if (__result && RoutePlanning.KeepGoing(__instance.Active))
            __result = false;
    }
}

/// <summary>Vehicle Framework's route planner for vehicles: the same pause, the same stop.</summary>
[HarmonyPatch]
internal static class VehicleRoutePlannerStartPatch
{
    private static MethodBase? Target() =>
        AccessTools.TypeByName("Vehicles.VehicleRoutePlanner") is { } type
            ? AccessTools.Method(type, "Start", new[] { typeof(Action), typeof(Action<PlanetTile>) })
            : null;

    private static bool Prepare() => Target() != null;

    private static MethodBase TargetMethod() => Target()!;

    private static void Prefix(out TimeSpeed __state) => __state = RoutePlanning.Before();

    private static void Postfix(TimeSpeed __state) => RoutePlanning.Restore(__state);
}

[HarmonyPatch]
internal static class VehicleRoutePlannerKeepPatch
{
    private static readonly Type? PlannerType = AccessTools.TypeByName("Vehicles.VehicleRoutePlanner");
    private static readonly PropertyInfo? ActiveProperty = PlannerType == null ? null : AccessTools.Property(PlannerType, "IsActive");

    private static MethodBase? Target() => PlannerType == null ? null : AccessTools.PropertyGetter(PlannerType, "ShouldStop");

    private static bool Prepare() => Target() != null;

    private static MethodBase TargetMethod() => Target()!;

    private static void Postfix(object __instance, ref bool __result)
    {
        if (__result && RoutePlanning.KeepGoing(ActiveProperty?.GetValue(__instance) is true))
            __result = false;
    }
}
