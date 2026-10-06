using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using RimMult.Shared.World;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;
using Verse.Sound;

namespace RimMult.Sync;

/// <summary>
/// InterRim Ballistic Missile against other players, without depending on it. Another player's colony on this globe is
/// only RimMult's marker, so a missile reaching it would hit nothing: when it arrives there, it goes to the defender
/// instead (a <see cref="ParcelAddress.ForMissile"/> parcel, at war and only to someone in the world, like a raid), and
/// the defender's game launches the same body and warhead at itself from the attacker's side — the way the mod's own
/// NPC factions strike the player: it flies over their globe, their defences may shoot it down, and it hits their base.
/// </summary>
internal static class MissileStrikes
{
    private static readonly Type? MissileType = AccessTools.TypeByName("IRBM.WorldObject_Missile");
    private static readonly Type? MissileCompType = AccessTools.TypeByName("IRBM.WorldObjectComp_Missile");
    private static readonly Type? BodyDefType = AccessTools.TypeByName("IRBM.MissileBodyDef");
    private static readonly Type? WarheadDefType = AccessTools.TypeByName("IRBM.MissileWarheadDef");
    private static readonly Type? LaunchTargetType = AccessTools.TypeByName("IRBM.LaunchTarget");
    private static readonly Type? AiUtility = AccessTools.TypeByName("IRBM.AIFactionMissileUtility");
    private static readonly Type? Targeting = AccessTools.TypeByName("IRBM.IRBM_TargetingUtility");
    private static readonly Type? LauncherType = AccessTools.TypeByName("IRBM.IIRBMLauncher");

    public static bool Available => MissileType != null && MissileCompType != null && AiUtility != null;

    /// <summary>Everything needed to queue a target at another player's colony is there.</summary>
    public static bool CanQueue => LauncherType != null && LaunchTargetType != null;

    public static MethodBase? OnArrived => MissileType == null ? null : AccessTools.Method(MissileType, "OnArrived");

    public static MethodBase? ChoseTactical => Targeting == null ? null : AccessTools.Method(Targeting, "ChoseTacticalWorldTarget");

    public static MethodBase? ChoseStrategic => Targeting == null ? null : AccessTools.Method(Targeting, "ChoseStrategicTarget");

    /// <summary>Another player's colony on this tile, if any.</summary>
    public static RemoteColony? ColonyAt(PlanetTile tile) =>
        Find.WorldObjects.AllWorldObjects.OfType<RemoteColony>().FirstOrDefault(c => c.Tile == tile);

    // ---------- attacker: the missile reaches the enemy's marker ----------

    /// <summary>A missile of ours arrived at another player's colony: it goes to them. False lets the mod carry on.</summary>
    public static bool TrySend(WorldObject missile)
    {
        var traverse = Traverse.Create(missile);
        if (missile.Faction != Faction.OfPlayer || traverse.Field<bool>("isAntiAir").Value)
            return false;
        var destination = traverse.Field<PlanetTile>("destinationTile").Value;
        if (!destination.Valid || ColonyAt(destination) is not { } colony)
            return false;

        var comp = missile.AllComps.FirstOrDefault(c => MissileCompType!.IsInstanceOfType(c));
        var body = comp == null ? null : Traverse.Create(comp).Field("bodyDef").GetValue<Def>();
        var warhead = comp == null ? null : Traverse.Create(comp).Field("warheadDef").GetValue<Def>();
        if (body == null || warhead == null)
            return false;

        // Where it came from: the defender's game launches it from there.
        var from = traverse.Field<PlanetTile>("initialTile").Value;
        if (!from.Valid)
        {
            var source = traverse.Field<int>("sourceTileID").Value;
            from = source >= 0 ? new PlanetTile(source, Find.WorldGrid.Surface) : Find.AnyPlayerHomeMap?.Tile ?? destination;
        }
        var label = missile.Label;
        if (Parcels.Send(colony.OwnerSteamId, colony.OwnerName, ParcelAddress.ForMissile(body.defName, warhead.defName, from.ToString()),
                new List<Thing>(), label, quiet: true, allowEmpty: true))
            Messages.Message("RimMult.MissileSent".Translate(label, colony.OwnerName), MessageTypeDefOf.NeutralEvent, historical: false);
        missile.Destroy();
        return true;
    }

    /// <summary>Aiming at another player's colony: only at an enemy (at war, PvP on, playing now).</summary>
    public static bool TargetAllowed(PlanetTile tile, out RemoteColony? colony)
    {
        colony = tile.Valid ? ColonyAt(tile) : null;
        if (colony == null)
            return true;
        var allowed = PlayerRaids.CanAttack(colony);
        if (!allowed.Accepted)
        {
            Messages.Message("RimMult.MissileNotAllowed".Translate(colony.OwnerName, allowed.FailReason), MessageTypeDefOf.RejectInput, historical: false);
            return false;
        }
        return true;
    }

    /// <summary>
    /// A tactical missile needs a map to pick a cell on, and another player's colony has none here: it is queued at the
    /// colony's tile without a cell (the defender's game picks where it lands), the way strategic targets are queued.
    /// </summary>
    public static bool QueueAtColony(object launcher, GlobalTargetInfo target)
    {
        // Through the interface: the launchers implement some of its members explicitly.
        object? Call(string name, params object[] args) => AccessTools.Method(LauncherType, name)?.Invoke(launcher, args);
        var entry = Call("GetCurrentSequenceEntry");
        if (Targeting != null && AccessTools.Method(Targeting, "IsTargetInRange") is { } inRange
            && inRange.Invoke(null, new[] { launcher, target, entry }) is false)
        {
            Messages.Message("IRBM.TargetOutOfRange".Translate(), MessageTypeDefOf.RejectInput, historical: false);
            return false;
        }
        var indexProperty = AccessTools.Property(LauncherType, "TargetingIndex");
        var index = (int)indexProperty.GetValue(launcher);
        var queued = Activator.CreateInstance(LaunchTargetType!);
        var t = Traverse.Create(queued);
        t.Field("tile").SetValue(target.Tile);
        t.Field("cell").SetValue(IntVec3.Invalid);
        t.Field("targetVec").SetValue(target.HasWorldObject ? target.WorldObject.DrawPos : Find.WorldGrid.GetTileCenter(target.Tile));
        t.Field("sequenceIndex").SetValue(Call("GetSequenceIndexFromTargetingIndex", index) is int sequence ? sequence : 0);
        var entryLauncher = entry == null ? null : Traverse.Create(entry).Field("launcher").GetValue<Building>();
        t.Field("launcher").SetValue(entryLauncher ?? launcher as Building);
        ((IList)AccessTools.Property(LauncherType, "QueuedTargets").GetValue(launcher)).Add(queued);
        SoundDefOf.Tick_High.PlayOneShotOnCamera();
        indexProperty.SetValue(launcher, index + 1);
        Call("TargetNext");
        return Call("GetTotalMissileCount") is int total && index + 1 >= total;
    }

    // ---------- defender: the strike arrives ----------

    /// <summary>An enemy's missile: launched at this colony from the enemy's side, or (refused) news for the attacker.</summary>
    public static bool Deliver(ParcelRecord parcel, string bodyName, string warheadName, string launchText)
    {
        if (parcel.Returned)
        {
            Find.LetterStack.ReceiveLetter("RimMult.MissileLostLabel".Translate(), "RimMult.MissileLostText".Translate(parcel.Summary), LetterDefOf.NeutralEvent);
            return true;
        }
        if (!Available || BodyDefType == null || WarheadDefType == null)
        {
            Log.Warning($"[RimMult] {parcel.FromName} fired a missile, but InterRim Ballistic Missile isn't loaded here.");
            return true;
        }
        var map = Find.Maps.FirstOrDefault(m => m.IsPlayerHome && m.mapPawns.FreeColonistsSpawnedCount > 0) ?? Find.AnyPlayerHomeMap;
        if (map?.Parent == null)
            return false; // nowhere to hit yet: it waits

        var body = GenDefDatabase.GetDefSilentFail(BodyDefType, bodyName);
        var warhead = GenDefDatabase.GetDefSilentFail(WarheadDefType, warheadName);
        if (body == null || warhead == null)
        {
            Log.Warning($"[RimMult] Unknown missile {bodyName} / {warheadName} from {parcel.FromName}.");
            return true;
        }
        if (!PlanetTile.TryParse(launchText, out var launchTile) || !launchTile.Valid)
            launchTile = Find.WorldObjects.AllWorldObjects.OfType<RemoteColony>().FirstOrDefault(c => c.OwnerSteamId == parcel.FromOwner)?.Tile ?? map.Tile;

        var drill = Traverse.Create(warhead).Field<bool>("drill").Value;
        var cell = AccessTools.Method(AiUtility, "SelectImpactCell")?.Invoke(null, new object[] { map, drill, true }) is IntVec3 picked ? picked : map.Center;
        var launch = AccessTools.Method(AiUtility, "LaunchMissile");
        if (launch == null)
            return true;
        try
        {
            launch.Invoke(null, new object?[]
            {
                PlayerRaids.RaidFaction(), body, warhead, launchTile, map.Tile, cell, map.Parent.DrawPos, null, null, null, map.Parent,
            });
        }
        catch (Exception e)
        {
            Log.Error($"[RimMult] Could not launch {parcel.FromName}'s missile here: {e.InnerException ?? e}");
            return true;
        }
        Find.LetterStack.ReceiveLetter("RimMult.MissileIncomingLabel".Translate(parcel.FromName),
            "RimMult.MissileIncomingText".Translate(parcel.FromName, parcel.Summary), LetterDefOf.ThreatBig,
            new LookTargets(new TargetInfo(cell, map)));
        return true;
    }
}

[HarmonyPatch]
internal static class MissileArrivedPatch
{
    private static bool Prepare() => MissileStrikes.Available && MissileStrikes.OnArrived != null;

    private static MethodBase TargetMethod() => MissileStrikes.OnArrived!;

    private static bool Prefix(WorldObject __instance) => !MissileStrikes.TrySend(__instance);
}

[HarmonyPatch]
internal static class MissileTacticalTargetPatch
{
    private static bool Prepare() => MissileStrikes.ChoseTactical != null && MissileStrikes.CanQueue;

    private static MethodBase TargetMethod() => MissileStrikes.ChoseTactical!;

    private static bool Prefix(object launcher, GlobalTargetInfo target, ref bool __result)
    {
        if (!target.IsValid || MissileStrikes.ColonyAt(target.Tile) == null)
            return true;
        __result = MissileStrikes.TargetAllowed(target.Tile, out _) && MissileStrikes.QueueAtColony(launcher, target);
        return false;
    }
}

[HarmonyPatch]
internal static class MissileStrategicTargetPatch
{
    private static bool Prepare() => MissileStrikes.ChoseStrategic != null;

    private static MethodBase TargetMethod() => MissileStrikes.ChoseStrategic!;

    private static bool Prefix(GlobalTargetInfo target, ref bool __result)
    {
        if (!target.IsValid || MissileStrikes.TargetAllowed(target.Tile, out _))
            return true;
        __result = false;
        return false;
    }
}
