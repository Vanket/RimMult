using HarmonyLib;
using RimMult.Sync;
using RimMult.UI;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;

namespace RimMult.Patches;

/// <summary>In the shared world your own colonies wear your player color too, so the globe reads at a glance.</summary>
internal static class OwnColors
{
    public static bool TryGet(WorldObject worldObject, out byte colorIndex)
    {
        colorIndex = 0;
        var session = Multiplayer.Session;
        if (!WorldSync.InWorld || session == null || worldObject is not Settlement || worldObject.Faction != Faction.OfPlayer)
            return false;
        colorIndex = session.ColorOf(session.PlayerId);
        return true;
    }
}

[HarmonyPatch(typeof(WorldObject), nameof(WorldObject.ExpandingIconColor), MethodType.Getter)]
internal static class OwnSettlementIconColorPatch
{
    private static void Postfix(WorldObject __instance, ref Color __result)
    {
        if (OwnColors.TryGet(__instance, out var index))
            __result = PlayerPalette.Get(index);
    }
}

[HarmonyPatch(typeof(Settlement), nameof(Settlement.Material), MethodType.Getter)]
internal static class OwnSettlementMaterialPatch
{
    private static void Postfix(Settlement __instance, ref Material __result)
    {
        // Never let coloring break the globe: an exception here stops the whole world interface from drawing
        // (that is what made "Form caravan" unusable).
        try
        {
            if (OwnColors.TryGet(__instance, out var index))
                __result = PlayerPalette.Tint(__result, index);
        }
        catch (System.Exception e)
        {
            Verse.Log.ErrorOnce($"[RimMult] Could not color the settlement: {e}", 0x52434F4C);
        }
    }
}
