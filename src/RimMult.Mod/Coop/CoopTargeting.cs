using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using RimMult.Shared.Coop;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace RimMult.Coop;

/// <summary>
/// Targeting gizmos (attack this, cast an ability, rescue, load into…) for co-op guests. The guest aims in its own copy
/// as usual; what it picks is sent to the host, which presses the same gizmo on the same thing and completes the
/// targeting at once with that target.
/// </summary>
internal static class CoopTargeting
{
    private sealed class Aim
    {
        public string Type = "";
        public string Label = "";
        public List<int> ThingIds = new();
        public int Frame;
    }

    private static readonly AccessTools.FieldRef<Targeter, Action<LocalTargetInfo>?> TargeterAction =
        AccessTools.FieldRefAccess<Targeter, Action<LocalTargetInfo>?>("action");

    private static readonly FieldInfo[] TargeterFields = AccessTools.GetDeclaredFields(typeof(Targeter)).Where(f => !f.IsStatic).ToArray();

    /// <summary>The gizmo the guest just pressed (its targeting starts this frame).</summary>
    private static Aim? _pressed;

    /// <summary>The gizmo the guest's targeter is aiming for right now.</summary>
    private static Aim? _armed;

    private static int _sentFrame = -1;

    /// <summary>Gizmos whose effect is "pick a target first".</summary>
    public static bool IsTargeting(Command command) => command is Command_Target or Command_VerbTarget or Command_Ability;

    // ---------- guest ----------

    /// <summary>The guest pressed a targeting gizmo: it runs in the copy (to aim), the pick goes to the host.</summary>
    public static void Pressed(Command command)
    {
        _pressed = new Aim
        {
            Type = command.GetType().FullName ?? "",
            Label = command.Label ?? "",
            ThingIds = Find.Selector.SelectedObjects.OfType<Thing>().Select(t => t.thingIDNumber).ToList(),
            Frame = Time.frameCount,
        };
    }

    /// <summary>
    /// After the pressed gizmo ran: if it didn't start aiming (an ability cast on oneself, …), it was a plain button
    /// and goes to the host as one.
    /// </summary>
    public static void AfterPressed(Command command)
    {
        if (_pressed == null || _pressed.Frame != Time.frameCount)
            return;
        var pressed = _pressed;
        _pressed = null;
        if (_armed == pressed)
            return;
        if (!Find.Targeter.IsTargeting && !Find.WorldTargeter.IsTargeting)
            CoopCommands.SendGizmo(command);
    }

    /// <summary>The guest's targeter started: if a guest gizmo started it, the pick is relayed instead of acted on.</summary>
    public static void TargetingBegan(Targeter targeter)
    {
        if (_pressed == null || _pressed.Frame != Time.frameCount)
        {
            _armed = null;
            return;
        }
        _armed = _pressed;
        var action = TargeterAction(targeter);
        if (action != null)
            TargeterAction(targeter) = target => Picked(target);
    }

    public static void TargetingStopped() => _armed = null;

    /// <summary>Guest: a verb (or ability) is about to be ordered at a target; true when that was relayed instead.</summary>
    public static bool RelayVerbOrder(LocalTargetInfo target)
    {
        if (_armed == null)
            return false;
        Picked(target);
        return true;
    }

    private static void Picked(LocalTargetInfo target)
    {
        var aim = _armed;
        // Several selected pawns each get the order in the same frame: the host gives it to all of them already.
        if (aim == null || Time.frameCount == _sentFrame)
            return;
        _sentFrame = Time.frameCount;
        var command = new CoopCommand
        {
            Kind = CoopCommandKind.Target,
            MapId = Find.CurrentMap?.uniqueID ?? -1,
            ThingIds = aim.ThingIds.ToList(),
            Name = aim.Type,
            Detail = aim.Label,
            Number = target.HasThing ? target.Thing.thingIDNumber : -1,
            Cells = { target.Cell.x, target.Cell.z },
            Queue = CoopCommands.QueueHeld(),
        };
        CoopCommands.Send(command);
    }

    // ---------- host ----------

    /// <summary>Presses the guest's gizmo on each of its things and completes the targeting with the guest's pick.</summary>
    public static void Execute(CoopCommand command, Map map)
    {
        var byId = CoopCommands.ThingsById(map);
        LocalTargetInfo target;
        if (command.Number >= 0)
        {
            if (!byId.TryGetValue(command.Number, out var targetThing))
                return;
            target = targetThing;
        }
        else if (command.Cells.Count >= 2)
        {
            var cell = new IntVec3(command.Cells[0], 0, command.Cells[1]);
            if (!cell.InBounds(map))
                return;
            target = cell;
        }
        else
        {
            return;
        }

        var targeter = Find.Targeter;
        var saved = TargeterFields.Select(f => f.GetValue(targeter)).ToArray();
        var selection = Find.Selector.SelectedObjects.ToList();
        try
        {
            foreach (var id in command.ThingIds)
            {
                if (!byId.TryGetValue(id, out var thing))
                    continue;
                var gizmo = thing.GetGizmos().OfType<Command>()
                    .FirstOrDefault(g => g.GetType().FullName == command.Name && g.Label == command.Detail);
                if (gizmo == null || gizmo.Disabled)
                    continue;

                // The gizmo aims for what is selected: just this thing, for this one press.
                Find.Selector.ClearSelection();
                Find.Selector.Select(thing, playSound: false, forceDesignatorDeselect: false);
                ClearTargeter(targeter);
                gizmo.ProcessInput(new Event());
                if (targeter.targetingSource is { } source)
                {
                    // Turrets take a forced target as an attack order, pawns' verbs and abilities as a job.
                    if (source.Caster is Building_Turret turret)
                        turret.OrderAttack(target);
                    else if (source.ValidateTarget(target, showMessages: false))
                        source.OrderForceTarget(target);
                }
                else if (TargeterAction(targeter) is { } action)
                {
                    action(target);
                }
                if (targeter.IsTargeting)
                    targeter.StopTargeting();
                CoopHost.Touch(thing);
            }
        }
        finally
        {
            for (var i = 0; i < TargeterFields.Length; i++)
                TargeterFields[i].SetValue(targeter, saved[i]);
            Find.Selector.ClearSelection();
            foreach (var selected in selection)
                Find.Selector.Select(selected, playSound: false, forceDesignatorDeselect: false);
        }
    }

    private static void ClearTargeter(Targeter targeter)
    {
        foreach (var field in TargeterFields)
        {
            var type = field.FieldType;
            if (type == typeof(List<Pawn>))
                field.SetValue(targeter, new List<Pawn>());
            else
                field.SetValue(targeter, type.IsValueType ? Activator.CreateInstance(type) : null);
        }
    }
}

/// <summary>Guest: notices when its targeter starts aiming for a gizmo the guest pressed, and when it stops.</summary>
[HarmonyPatch]
internal static class CoopTargeterPatches
{
    private static IEnumerable<MethodBase> TargetMethods() =>
        AccessTools.GetDeclaredMethods(typeof(Targeter)).Where(m => m.Name == nameof(Targeter.BeginTargeting));

    private static void Postfix(Targeter __instance)
    {
        if (CoopGuest.Active && !CoopGuest.Applying)
            CoopTargeting.TargetingBegan(__instance);
    }
}

[HarmonyPatch(typeof(Targeter), nameof(Targeter.StopTargeting))]
internal static class CoopTargeterStopPatch
{
    private static void Postfix()
    {
        if (CoopGuest.Active)
            CoopTargeting.TargetingStopped();
    }
}

/// <summary>
/// Guest: the targeter confirming a pick for a targeting source of any kind (a verb, a vanilla ability, a mod's own
/// ability like Vanilla Psycasts Expanded's): the pick is relayed to the host instead of ordered here.
/// </summary>
[HarmonyPatch(typeof(Targeter), "OrderVerbForceTarget")]
internal static class CoopTargeterOrderPatch
{
    private static readonly Func<Targeter, bool, LocalTargetInfo> UnderMouse =
        AccessTools.MethodDelegate<Func<Targeter, bool, LocalTargetInfo>>(AccessTools.Method(typeof(Targeter), "CurrentTargetUnderMouse"));

    private static bool Prefix(Targeter __instance)
    {
        if (!CoopGuest.Active || CoopGuest.Applying || CoopGuest.Visiting)
            return true;
        var target = UnderMouse(__instance, false);
        return !target.IsValid || !CoopTargeting.RelayVerbOrder(target);
    }
}

/// <summary>Guest: a verb or ability ordered at what the guest picked is relayed to the host instead.</summary>
[HarmonyPatch]
[Patches.LatePatch]
internal static class CoopVerbOrderPatch
{
    private static IEnumerable<MethodBase> TargetMethods()
    {
        foreach (var type in typeof(Verb).AllSubclasses().Prepend(typeof(Verb)))
        {
            if (type.ContainsGenericParameters)
                continue;
            var method = AccessTools.DeclaredMethod(type, nameof(Verb.OrderForceTarget), new[] { typeof(LocalTargetInfo) });
            if (method != null && !method.IsAbstract)
                yield return method;
        }
    }

    private static bool Prefix(LocalTargetInfo __0) =>
        !CoopGuest.Active || CoopGuest.Applying || !CoopTargeting.RelayVerbOrder(__0);
}

/// <summary>Guest: a turret's forced target picked by the guest is relayed to the host instead.</summary>
[HarmonyPatch]
[Patches.LatePatch]
internal static class CoopTurretOrderPatch
{
    private static IEnumerable<MethodBase> TargetMethods()
    {
        foreach (var type in typeof(Building_Turret).AllSubclasses().Prepend(typeof(Building_Turret)))
        {
            if (type.ContainsGenericParameters)
                continue;
            var method = AccessTools.DeclaredMethod(type, nameof(Building_Turret.OrderAttack), new[] { typeof(LocalTargetInfo) });
            if (method != null && !method.IsAbstract)
                yield return method;
        }
    }

    private static bool Prefix(LocalTargetInfo __0) =>
        !CoopGuest.Active || CoopGuest.Applying || !CoopTargeting.RelayVerbOrder(__0);
}

/// <summary>Host: a guest's order given with Shift held is queued after the pawn's current orders.</summary>
[HarmonyPatch]
internal static class CoopQueueKeyPatch
{
    [HarmonyPostfix]
    [HarmonyPatch(typeof(KeyBindingDef), nameof(KeyBindingDef.IsDownEvent), MethodType.Getter)]
    private static void DownEvent(KeyBindingDef __instance, ref bool __result) => Override(__instance, ref __result);

    [HarmonyPostfix]
    [HarmonyPatch(typeof(KeyBindingDef), nameof(KeyBindingDef.IsDown), MethodType.Getter)]
    private static void Down(KeyBindingDef __instance, ref bool __result) => Override(__instance, ref __result);

    [HarmonyPrefix]
    [HarmonyPatch(typeof(Pawn_JobTracker), nameof(Pawn_JobTracker.TryTakeOrderedJob))]
    private static void Ordered(ref bool requestQueueing)
    {
        if (CoopCommands.QueueOverride == true)
            requestQueueing = true;
    }

    private static void Override(KeyBindingDef def, ref bool result)
    {
        if (CoopCommands.QueueOverride is { } queue && def == KeyBindingDefOf.QueueOrder)
            result = queue;
    }
}
