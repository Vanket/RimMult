using System;
using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using Verse;

namespace RimMult.Patches;

/// <summary>
/// "Form caravan" does nothing for some players. These patches write what happens to the log (button state, the
/// press, the window opening, any error) so a <c>Player.log</c> shows where it stops. They change nothing else.
/// </summary>
[HarmonyPatch]
internal static class CaravanDiagnostics
{
    private static string _lastState = "";

    [HarmonyPostfix]
    [HarmonyPatch(typeof(FormCaravanComp), nameof(FormCaravanComp.GetGizmos))]
    private static IEnumerable<Gizmo> Gizmos(IEnumerable<Gizmo> __result, FormCaravanComp __instance)
    {
        foreach (var gizmo in __result)
        {
            if (gizmo is Command_Action command && command.action is { } action && !(command.action.Target is Wrapped))
            {
                var state = $"{command.defaultLabel} at {__instance.parent?.Label}: disabled={command.Disabled} {command.disabledReason}";
                if (state != _lastState)
                {
                    _lastState = state;
                    Log.Message($"[RimMult] Caravan button: {state}");
                }
                command.action = new Wrapped(command.defaultLabel, action).Run;
            }
            yield return gizmo;
        }
    }

    private sealed class Wrapped
    {
        private readonly string _label;
        private readonly Action _action;

        public Wrapped(string label, Action action)
        {
            _label = label;
            _action = action;
        }

        public void Run()
        {
            Log.Message($"[RimMult] Caravan button pressed: {_label}");
            try
            {
                _action();
            }
            catch (Exception e)
            {
                Log.Error($"[RimMult] Caravan button '{_label}' failed: {e}");
            }
        }
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(Dialog_FormCaravan), nameof(Dialog_FormCaravan.PostOpen))]
    private static void Opened() => Log.Message("[RimMult] Form caravan window opened");

    [HarmonyFinalizer]
    [HarmonyPatch(typeof(Dialog_FormCaravan), nameof(Dialog_FormCaravan.PostOpen))]
    private static Exception? OpenFailed(Exception? __exception)
    {
        if (__exception != null)
            Log.Error($"[RimMult] Form caravan window failed to open: {__exception}");
        return __exception;
    }

    [HarmonyFinalizer]
    [HarmonyPatch(typeof(Dialog_FormCaravan), nameof(Dialog_FormCaravan.DoWindowContents))]
    private static Exception? DrawFailed(Exception? __exception)
    {
        if (__exception != null)
            Log.ErrorOnce($"[RimMult] Form caravan window failed to draw: {__exception}", 0x46434456);
        return __exception;
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(Dialog_FormCaravan), nameof(Dialog_FormCaravan.PostClose))]
    private static void Closed() => Log.Message("[RimMult] Form caravan window closed");
}
