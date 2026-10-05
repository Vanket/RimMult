using System.Reflection;
using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using Verse;

namespace RimMult.Patches;

/// <summary>
/// A caravan formed at home must have a route in 1.6. The game opens the route planner by itself when the window
/// opens, but Vehicle Framework takes that out (and sends vehicle caravans itself): "Send" then only says "choose a
/// route first", and players don't find the "Change route" button. When sending fails for that reason alone, the
/// route planner opens right away.
/// </summary>
internal static class CaravanRoute
{
    private static readonly MethodInfo? MustChooseRouteGetter = AccessTools.PropertyGetter(typeof(Dialog_FormCaravan), "MustChooseRoute");
    private static readonly AccessTools.FieldRef<Dialog_FormCaravan, PlanetTile> Destination =
        AccessTools.FieldRefAccess<Dialog_FormCaravan, PlanetTile>("destinationTile");

    /// <summary>Sending didn't happen: if only the route is missing, choose it now.</summary>
    public static void OfferRoute(Dialog_FormCaravan dialog)
    {
        if (!dialog.IsOpen || Destination(dialog).Valid || MustChooseRouteGetter?.Invoke(dialog, null) is not true)
            return;
        Find.WorldRoutePlanner.Start(dialog);
    }
}

[HarmonyPatch(typeof(Dialog_FormCaravan), "CheckForErrors")]
internal static class CaravanRouteCheckPatch
{
    private static void Postfix(Dialog_FormCaravan __instance, bool __result)
    {
        if (!__result)
            CaravanRoute.OfferRoute(__instance);
    }
}

/// <summary>Vehicle Framework sends vehicle caravans itself, with its own "choose a route first".</summary>
[HarmonyPatch]
internal static class CaravanRouteVehiclesPatch
{
    private static MethodBase? Target() =>
        AccessTools.TypeByName("Vehicles.CaravanFormation") is { } type ? AccessTools.Method(type, "TrySendVehicleCaravan", new[] { typeof(Dialog_FormCaravan) }) : null;

    private static bool Prepare() => Target() != null;

    private static MethodBase TargetMethod() => Target()!;

    private static void Postfix(Dialog_FormCaravan formCaravan) => CaravanRoute.OfferRoute(formCaravan);
}
