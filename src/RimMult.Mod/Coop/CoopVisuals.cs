using System;
using System.Collections.Generic;
using HarmonyLib;
using RimMult.Shared.Coop;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.Sound;

namespace RimMult.Coop;

/// <summary>
/// What a co-op guest sees move, without simulating it: pawns glide along the positions the host draws them at,
/// and shots fly from the shooter to where the bullet lands, with the weapon's sound.
/// </summary>
internal static class CoopVisuals
{
    private sealed class Glide
    {
        public Vector3 From;
        public Vector3 To;
        public float Start;
        public float Duration = 0.1f;
        public float LastUpdate;
    }

    private sealed class FlyingShot
    {
        public int MapId;
        public ThingDef Def = null!;
        public Vector3 From;
        public Vector3 To;
        public float Progress;
        public float Ticks;
    }

    private const float MinGlide = 0.02f;
    private const float MaxGlide = 0.25f;

    private static readonly Dictionary<int, Glide> Glides = new();
    private static readonly List<FlyingShot> FlyingShots = new();
    private static List<AimMark> _aims = new();
    private static List<ProgressMark> _bars = new();
    private static readonly Vector2 BarSize = new(0.8f, 0.14f);
    private static Material? _barFilled;
    private static Material? _barUnfilled;

    public static void Reset()
    {
        Glides.Clear();
        FlyingShots.Clear();
        _aims = new List<AimMark>();
        _bars = new List<ProgressMark>();
    }

    /// <summary>The host's current aims and progress bars (replacing the previous ones).</summary>
    public static void OnMarks(List<AimMark> aims, List<ProgressMark> bars)
    {
        _aims = aims;
        _bars = bars;
    }

    /// <summary>Aim pies (and lines for selected shooters) and work progress bars, as the host sees them.</summary>
    public static void DrawMarks(Map map)
    {
        if (_aims.Count > 0)
        {
            Dictionary<int, Pawn>? pawns = null;
            foreach (var aim in _aims)
            {
                if (aim.MapId != map.uniqueID)
                    continue;
                pawns ??= BuildPawns(map);
                if (!pawns.TryGetValue(aim.ShooterId, out var shooter))
                    continue;
                var target = new Vector3(aim.TargetX / 100f, 0f, aim.TargetZ / 100f);
                var cell = target.ToIntVec3();
                if (!cell.InBounds(map))
                    continue;
                if (aim.Degrees > 0)
                    GenDraw.DrawAimPie(shooter, new LocalTargetInfo(cell), aim.Degrees, 0.2f);
                if (Find.Selector.IsSelected(shooter))
                    GenDraw.DrawLineBetween(shooter.DrawPos, target.WithY(AltitudeLayer.MetaOverlays.AltitudeFor()));
            }
        }

        if (_bars.Count > 0)
        {
            _barFilled ??= SolidColorMaterials.SimpleSolidColorMaterial(new Color(0.9f, 0.85f, 0.2f));
            _barUnfilled ??= SolidColorMaterials.SimpleSolidColorMaterial(new Color(0.3f, 0.3f, 0.3f));
            var altitude = AltitudeLayer.MetaOverlays.AltitudeFor();
            foreach (var bar in _bars)
            {
                if (bar.MapId != map.uniqueID)
                    continue;
                GenDraw.DrawFillableBar(new GenDraw.FillableBarRequest
                {
                    center = new Vector3(bar.X / 100f, altitude, bar.Z / 100f),
                    size = BarSize,
                    fillPercent = bar.Percent / 100f,
                    filledMat = _barFilled,
                    unfilledMat = _barUnfilled,
                    margin = 0.04f,
                    rotation = Rot4.North,
                });
            }
        }
    }

    private static Dictionary<int, Pawn> BuildPawns(Map map)
    {
        var pawns = new Dictionary<int, Pawn>();
        foreach (var pawn in map.mapPawns.AllPawnsSpawned)
            pawns[pawn.thingIDNumber] = pawn;
        return pawns;
    }

    /// <summary>The host drew this pawn at <paramref name="target"/> just now: glide there over about one update interval.</summary>
    public static void OnPosition(int pawnId, Vector3 target)
    {
        var now = Time.realtimeSinceStartup;
        if (!Glides.TryGetValue(pawnId, out var glide))
        {
            Glides[pawnId] = new Glide { From = target, To = target, Start = now, LastUpdate = now };
            return;
        }
        glide.From = Current(glide, now);
        glide.To = target;
        glide.Start = now;
        glide.Duration = Mathf.Clamp(now - glide.LastUpdate, MinGlide, MaxGlide);
        glide.LastUpdate = now;
    }

    public static bool TryGetDrawPos(int pawnId, out Vector3 position)
    {
        if (Glides.TryGetValue(pawnId, out var glide))
        {
            position = Current(glide, Time.realtimeSinceStartup);
            return true;
        }
        position = default;
        return false;
    }

    private static Vector3 Current(Glide glide, float now) =>
        Vector3.Lerp(glide.From, glide.To, Mathf.Clamp01((now - glide.Start) / glide.Duration));

    public static void OnShot(Map map, CoopShot shot)
    {
        var def = DefDatabase<ThingDef>.GetNamedSilentFail(shot.ProjectileDef);
        if (def == null || shot.Ticks <= 0)
            return;
        var from = new Vector3(shot.FromX, 0f, shot.FromZ);
        FlyingShots.Add(new FlyingShot
        {
            MapId = map.uniqueID,
            Def = def,
            From = from,
            To = new Vector3(shot.ToX, 0f, shot.ToZ),
            Ticks = shot.Ticks,
        });
        if (shot.Sound.Length > 0 && DefDatabase<SoundDef>.GetNamedSilentFail(shot.Sound) is { } sound)
        {
            var cell = from.ToIntVec3();
            if (cell.InBounds(map))
                sound.PlayOneShot(new TargetInfo(cell, map));
        }
    }

    /// <summary>Moves and draws the shots in flight on <paramref name="map"/> (called every frame).</summary>
    public static void DrawShots(Map map)
    {
        if (FlyingShots.Count == 0)
            return;
        var tickManager = Find.TickManager;
        var ticksThisFrame = tickManager.Paused ? 0f : Time.deltaTime * 60f * tickManager.TickRateMultiplier;
        var altitude = Altitudes.AltitudeFor(AltitudeLayer.Projectile);

        for (var i = FlyingShots.Count - 1; i >= 0; i--)
        {
            var shot = FlyingShots[i];
            shot.Progress += ticksThisFrame / shot.Ticks;
            if (shot.Progress >= 1f)
            {
                FlyingShots.RemoveAt(i);
                continue;
            }
            if (shot.MapId != map.uniqueID)
                continue;
            try
            {
                var position = Vector3.Lerp(shot.From, shot.To, shot.Progress);
                position.y = altitude;
                var direction = shot.To - shot.From;
                var rotation = direction.sqrMagnitude > 0.0001f ? Quaternion.LookRotation(direction) : Quaternion.identity;
                var size = shot.Def.graphicData?.drawSize ?? Vector2.one;
                Graphics.DrawMesh(MeshPool.GridPlane(size), position, rotation, shot.Def.DrawMatSingle, 0);
            }
            catch (Exception)
            {
                FlyingShots.RemoveAt(i);
            }
        }
    }
}

/// <summary>Guest: pawns are drawn where the host draws them (smoothly), not snapped to the cell they stand on.</summary>
[HarmonyPatch(typeof(Pawn_DrawTracker), nameof(Pawn_DrawTracker.DrawPos), MethodType.Getter)]
internal static class CoopDrawPosPatch
{
    private static readonly AccessTools.FieldRef<Pawn_DrawTracker, Pawn> TrackerPawn = AccessTools.FieldRefAccess<Pawn_DrawTracker, Pawn>("pawn");

    private static void Postfix(Pawn_DrawTracker __instance, ref Vector3 __result)
    {
        if (!CoopGuest.Active)
            return;
        var pawn = TrackerPawn(__instance);
        if (pawn != null && pawn.Spawned && CoopVisuals.TryGetDrawPos(pawn.thingIDNumber, out var position))
        {
            __result.x = position.x;
            __result.z = position.z;
        }
    }
}

/// <summary>Guest: shots in flight are drawn with the map.</summary>
[HarmonyPatch(typeof(Map), nameof(Map.MapUpdate))]
internal static class CoopShotsDrawPatch
{
    private static void Postfix(Map __instance)
    {
        if (CoopGuest.Active && __instance == Find.CurrentMap)
        {
            CoopVisuals.DrawShots(__instance);
            CoopVisuals.DrawMarks(__instance);
        }
    }
}

/// <summary>Host: every launched projectile becomes a shot for the guests to draw.</summary>
[HarmonyPatch(typeof(Projectile), nameof(Projectile.Launch),
    new[] { typeof(Thing), typeof(Vector3), typeof(LocalTargetInfo), typeof(LocalTargetInfo), typeof(ProjectileHitFlags), typeof(bool), typeof(Thing), typeof(ThingDef) })]
internal static class CoopShotPatch
{
    private static readonly AccessTools.FieldRef<Projectile, Vector3> Destination = AccessTools.FieldRefAccess<Projectile, Vector3>("destination");
    private static readonly AccessTools.FieldRef<Projectile, int> TicksToImpact = AccessTools.FieldRefAccess<Projectile, int>("ticksToImpact");

    private static void Postfix(Projectile __instance, Thing launcher, Vector3 origin, Thing equipment)
    {
        if (!CoopHost.Active)
            return;
        try
        {
            var sound = equipment?.TryGetComp<CompEquippable>()?.PrimaryVerb?.verbProps?.soundCast
                        ?? (launcher as Pawn)?.CurrentEffectiveVerb?.verbProps?.soundCast
                        ?? (launcher as Building_TurretGun)?.AttackVerb?.verbProps?.soundCast;
            CoopHost.NotifyShot(__instance, origin, Destination(__instance), TicksToImpact(__instance), sound);
        }
        catch (Exception e)
        {
            Log.WarningOnce($"[RimMult] Co-op: could not share a shot: {e.Message}", 0x434F5348);
        }
    }
}

/// <summary>
/// Guest: the copy's own stances are frozen (it doesn't tick), so their aim pies would hang forever; the host's live
/// aims are drawn instead (see <see cref="CoopVisuals.DrawMarks"/>).
/// </summary>
[HarmonyPatch(typeof(Pawn_StanceTracker), nameof(Pawn_StanceTracker.StanceTrackerDraw))]
internal static class CoopStanceDrawPatch
{
    private static bool Prefix() => !CoopGuest.Active;
}
