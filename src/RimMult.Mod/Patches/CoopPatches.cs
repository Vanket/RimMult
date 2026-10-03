using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using RimMult.Coop;
using RimWorld;
using UnityEngine;
using Verse;

namespace RimMult.Patches;

/// <summary>Host: what spawns and despawns is streamed to guests. Guest: strays left by a replaced thing are noticed.</summary>
[HarmonyPatch(typeof(Thing))]
internal static class CoopSpawnPatches
{
    [HarmonyPostfix]
    [HarmonyPatch(nameof(Thing.SpawnSetup))]
    private static void Spawned(Thing __instance, bool respawningAfterLoad)
    {
        if (CoopGuest.Applying)
            CoopGuest.NotifySpawned(__instance);
        else if (!respawningAfterLoad)
            CoopHost.NotifySpawned(__instance);
    }

    [HarmonyPrefix]
    [HarmonyPatch(nameof(Thing.DeSpawn))]
    private static void Despawning(Thing __instance) => CoopHost.NotifyDespawned(__instance);
}

/// <summary>
/// A guest's copy of the game never simulates: the host's game is the only one that runs. The clock still "runs"
/// at the shared speed (so pawns glide between the positions the host sends), only the ticks are skipped.
/// </summary>
[HarmonyPatch(typeof(TickManager), nameof(TickManager.DoSingleTick))]
internal static class CoopTickPatch
{
    private static bool Prefix() => !CoopGuest.Active;
}

/// <summary>The guest's copy is not theirs to keep while playing: saving would only save a stale copy.</summary>
[HarmonyPatch(typeof(GameDataSaveLoader), nameof(GameDataSaveLoader.SaveGame))]
internal static class CoopNoSavePatch
{
    private static bool Prefix()
    {
        if (!CoopGuest.Active)
            return true;
        Messages.Message("RimMult.CoopNoSave".Translate(), MessageTypeDefOf.RejectInput, historical: false);
        return false;
    }
}

/// <summary>
/// Guest orders that live in overridable methods (every designator's Designate*, every gizmo's ProcessInput, mods'
/// included). Patched one by one, so a mod's odd override can't stop the rest from being patched.
/// </summary>
internal static class CoopOverridePatches
{
    public static void Apply(Harmony harmony)
    {
        var designate = new HashSet<string> { nameof(Designator.DesignateSingleCell), nameof(Designator.DesignateMultiCell), nameof(Designator.DesignateThing) };
        var designatePrefix = new HarmonyMethod(typeof(CoopDesignatorPatch), nameof(CoopDesignatorPatch.Prefix));
        foreach (var method in Patchable(typeof(Designator), m => designate.Contains(m.Name)))
            TryPatch(harmony, method, designatePrefix);

        var gizmoPrefix = new HarmonyMethod(typeof(CoopGizmoPatch), nameof(CoopGizmoPatch.Prefix));
        foreach (var method in Patchable(typeof(Command), m => m.Name == nameof(Command.ProcessInput)
                                                             && m.GetParameters().Length == 1
                                                             && m.GetParameters()[0].ParameterType == typeof(Event)))
            TryPatch(harmony, method, gizmoPrefix);
    }

    private static void TryPatch(Harmony harmony, MethodBase method, HarmonyMethod prefix)
    {
        try
        {
            harmony.Patch(method, prefix: prefix);
        }
        catch (Exception e)
        {
            Log.Warning($"[RimMult] Co-op: could not patch {method.DeclaringType?.FullName}.{method.Name}: {e.Message}");
        }
    }

    /// <summary>Non-abstract methods of <paramref name="baseType"/> and every subclass that declare an override.</summary>
    private static IEnumerable<MethodBase> Patchable(Type baseType, Func<MethodInfo, bool> match)
    {
        const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly;
        foreach (var type in AccessTools.AllTypes())
        {
            if (!baseType.IsAssignableFrom(type) || type.ContainsGenericParameters)
                continue;
            MethodInfo[] methods;
            try
            {
                methods = type.GetMethods(flags);
            }
            catch (Exception)
            {
                continue;
            }
            foreach (var method in methods)
            {
                if (!method.IsAbstract && method.HasMethodBody() && !method.ContainsGenericParameters && match(method))
                    yield return method;
            }
        }
    }
}

/// <summary>Guest: designating (mine, build, zone, …) becomes an order for the host.</summary>
internal static class CoopDesignatorPatch
{
    public static bool Prefix(Designator __instance, MethodBase __originalMethod, object[] __args)
    {
        if (!CoopGuest.Active || CoopGuest.Applying)
            return true;

        switch (__originalMethod.Name)
        {
            case nameof(Designator.DesignateSingleCell) when __args[0] is IntVec3 cell:
                CoopCommands.SendDesignate(__instance, new[] { cell }, null);
                break;
            case nameof(Designator.DesignateMultiCell) when __args[0] is IEnumerable<IntVec3> cells:
                CoopCommands.SendDesignate(__instance, cells.ToList(), null);
                break;
            case nameof(Designator.DesignateThing) when __args[0] is Thing thing:
                CoopCommands.SendDesignate(__instance, null, thing);
                break;
        }
        return false;
    }
}

/// <summary>
/// Guest: gizmo buttons (draft, forbid, …) become orders for the host. Designator buttons stay local (they only pick
/// the designator); targeting commands (attack this, cast at…) can't be relayed yet and are refused.
/// </summary>
internal static class CoopGizmoPatch
{
    public static bool Prefix(Command __instance)
    {
        // Host: whatever the button changes on the selection goes to the guests at once (medical bed, forbid, …).
        if (CoopHost.Active && __instance is not Designator)
            foreach (var thing in Find.Selector.SelectedObjects.OfType<Thing>())
                CoopHost.Touch(thing);
        if (!CoopGuest.Active || CoopGuest.Applying || __instance is Designator)
            return true;
        if (__instance is Command_Target or Command_VerbTarget)
        {
            Messages.Message("RimMult.CoopTargetUnsupported".Translate(), MessageTypeDefOf.RejectInput, historical: false);
            return false;
        }
        CoopCommands.SendGizmo(__instance);
        Log.Message($"[RimMult] Co-op guest: button '{__instance.Label}' sent to the host");
        return false;
    }
}

/// <summary>Guest: a right-click order on the map is sent to the host, which picks the same option by label.</summary>
[HarmonyPatch]
internal static class CoopFloatMenuPatch
{
    private sealed class Origin
    {
        public Origin(List<Pawn> pawns, Vector3 clickPos)
        {
            Pawns = pawns;
            ClickPos = clickPos;
        }

        public List<Pawn> Pawns { get; }
        public Vector3 ClickPos { get; }
    }

    // Weak: the map menu re-creates its options every few frames; old ones just fall away.
    private static readonly ConditionalWeakTable<FloatMenuOption, Origin> Origins = new();

    [HarmonyPostfix]
    [HarmonyPatch(typeof(FloatMenuMakerMap), nameof(FloatMenuMakerMap.GetOptions))]
    private static void Remember(List<Pawn> selectedPawns, Vector3 clickPos, List<FloatMenuOption> __result)
    {
        if (!CoopGuest.Active || __result == null)
            return;
        var origin = new Origin(selectedPawns.ToList(), clickPos);
        foreach (var option in __result)
        {
            Origins.Remove(option);
            Origins.Add(option, origin);
        }
    }

    [HarmonyPrefix]
    [HarmonyPatch(typeof(FloatMenuOption), nameof(FloatMenuOption.Chosen))]
    private static bool Chosen(FloatMenuOption __instance)
    {
        if (!CoopGuest.Active || CoopGuest.Applying || !Origins.TryGetValue(__instance, out var origin))
            return true;
        if (!__instance.Disabled)
            CoopCommands.SendFloatMenu(__instance, origin.Pawns, origin.ClickPos);
        return false;
    }
}

/// <summary>Guest: work priorities and research also change on the host (and locally, so the change shows at once).</summary>
[HarmonyPatch]
internal static class CoopSettingsPatches
{
    [HarmonyPrefix]
    [HarmonyPatch(typeof(Pawn_WorkSettings), nameof(Pawn_WorkSettings.SetPriority))]
    private static void WorkPriority(Pawn_WorkSettings __instance, WorkTypeDef w, int priority)
    {
        if (CoopGuest.Active && !CoopGuest.Applying)
            CoopCommands.SendWorkPriority(__instance, w, priority);
    }

    [HarmonyPrefix]
    [HarmonyPatch(typeof(ResearchManager), nameof(ResearchManager.SetCurrentProject))]
    private static void Research(ResearchProjectDef proj)
    {
        if (CoopGuest.Active && !CoopGuest.Applying)
            CoopCommands.SendResearch(proj);
    }
}
