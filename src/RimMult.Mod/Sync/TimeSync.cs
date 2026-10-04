using System;
using HarmonyLib;
using RimMult.ClientCore;
using RimMult.Shared.Time;
using UnityEngine;
using Verse;

namespace RimMult.Sync;

/// <summary>
/// Keeps this game's clock on the shared world clock while in the world:
/// <list type="bullet">
/// <item>the speed buttons (and hotkeys, space) become votes; the game runs at the speed the server resolves;</item>
/// <item>the game does not tick past the server's horizon (see <see cref="Patches.TimePatches"/>);</item>
/// <item>reports where this colony is and how fast this machine actually manages to run it.</item>
/// </list>
/// </summary>
internal static class TimeSync
{
    /// <summary>Frequent reports keep the server's view of this colony fresh, so others rarely wait for it.</summary>
    private const float ReportInterval = 0.1f;
    private const float MeasureWindow = 1f;

    /// <summary>Reported until a real measurement exists: "not a bottleneck".</summary>
    private const float UnknownRate = 100_000f;

    /// <summary>
    /// Closer to the horizon than this share of the allowed lead, the game eases off instead of running into the
    /// horizon and stopping dead (a stop-and-go a few times a second, seen as hitches).
    /// </summary>
    private const float EaseFrom = 0.6f;

    /// <summary>Never slower than this while easing off: the horizon itself still stops the game.</summary>
    private const float MinFactor = 0.05f;

    private static readonly Func<TickManager, bool>? NothingHappening = AccessTools.Method(typeof(TickManager), "NothingHappeningInGame") is { } method
        ? AccessTools.MethodDelegate<Func<TickManager, bool>>(method)
        : null;

    private static TimeSpeed _applied;
    private static bool _hasApplied;
    private static long _horizon;
    private static float _lastReport;
    private static float _windowStart;
    private static int _windowStartTick;
    private static float _idleTime;
    private static float _lastFrame;
    private static float _sustainableRate = UnknownRate;

    public static bool Active { get; private set; }

    /// <summary>The speed this player voted for, shown next to the speed that was resolved.</summary>
    public static GameSpeed? MyVote { get; private set; }

    /// <summary>True while this colony is ahead of the others and waits for them.</summary>
    public static bool BlockedByHorizon => Active && Find.TickManager != null && Find.TickManager.TicksAbs >= _horizon;

    /// <summary>Superfast may run doubled (RimWorld's own "nothing happening" speed-up): the server says so for everyone.</summary>
    public static bool Boost { get; private set; }

    /// <summary>How much of its speed this game uses (1: all; less while ahead of the others, see <see cref="EaseFrom"/>).</summary>
    public static float SpeedFactor { get; private set; } = 1f;

    /// <summary>This colony waits for the others, stopped or slowed down.</summary>
    public static bool Waiting => Active && (BlockedByHorizon || SpeedFactor < 0.9f);

    public static void Begin()
    {
        var tickManager = Find.TickManager;
        Active = true;
        Boost = false;
        SpeedFactor = 1f;
        _hasApplied = false;
        _horizon = tickManager.TicksAbs;
        _sustainableRate = UnknownRate;
        MyVote = null;
        StartWindow(Time.realtimeSinceStartup, tickManager.TicksAbs);
    }

    public static void End()
    {
        Active = false;
        Boost = false;
        SpeedFactor = 1f;
        MyVote = null;
    }

    public static void Update(ClientSession session)
    {
        var tickManager = Find.TickManager;
        if (!Active || tickManager == null)
            return;

        var grant = session.LastGrant;
        if (grant != null)
            _horizon = grant.HorizonTick;

        // Anything that changed the speed since our last frame (buttons, hotkeys, space) was the player: a vote.
        var observed = tickManager.CurTimeSpeed;
        if (_hasApplied && observed != _applied)
        {
            MyVote = (GameSpeed)(byte)observed;
            session.VoteSpeed(MyVote);
        }

        // Our pause was lifted by someone else: it no longer is our vote.
        if (MyVote == GameSpeed.Paused && grant != null && grant.Speed != GameSpeed.Paused)
            MyVote = null;

        var target = (TimeSpeed)(byte)(grant?.Speed ?? GameSpeed.Paused);
        if (tickManager.CurTimeSpeed != target)
            tickManager.CurTimeSpeed = target;
        _applied = target;
        _hasApplied = true;
        Boost = grant?.Boost ?? false;
        SpeedFactor = Ease(session, tickManager, target);

        Measure(tickManager, target);

        var now = Time.realtimeSinceStartup;
        if (now - _lastReport >= ReportInterval)
        {
            _lastReport = now;
            session.ReportAuthority(tickManager.TicksAbs, _sustainableRate, Idle(tickManager));
        }
    }

    /// <summary>
    /// Running into the horizon at full speed means stopping dead, then going again as soon as it moves: hitches.
    /// Ahead of the others (little room left before the horizon), the game eases off to their pace instead.
    /// </summary>
    private static float Ease(ClientSession session, TickManager tickManager, TimeSpeed target)
    {
        if (target == TimeSpeed.Paused || session.TimeSettings is not { } settings)
            return 1f;
        var lead = settings.DriftTicks(((GameSpeed)(byte)target).TicksPerSecond(Boost));
        var room = _horizon - tickManager.TicksAbs;
        var easeFrom = lead * EaseFrom;
        return room >= easeFrom ? 1f : Mathf.Clamp(room / easeFrom, MinFactor, 1f);
    }

    /// <summary>Nothing happens in this colony (RimWorld would double Superfast): everyone in the world must be so.</summary>
    private static bool Idle(TickManager tickManager)
    {
        try
        {
            return NothingHappening?.Invoke(tickManager) ?? false;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// Ticks per second actually achieved while the game was allowed to run. Time spent paused or waiting at the
    /// horizon doesn't count, so a fast machine that waits for others is not mistaken for a slow one.
    /// </summary>
    private static void Measure(TickManager tickManager, TimeSpeed target)
    {
        var now = Time.realtimeSinceStartup;
        var frame = now - _lastFrame;
        _lastFrame = now;
        if (target == TimeSpeed.Paused || tickManager.Paused)
            _idleTime += frame;
        else
            _idleTime += frame * (1f - SpeedFactor); // easing off to wait for the others isn't slowness either

        var elapsed = now - _windowStart;
        if (elapsed < MeasureWindow)
            return;

        var running = elapsed - _idleTime;
        if (running > MeasureWindow * 0.5f)
            _sustainableRate = (tickManager.TicksAbs - _windowStartTick) / running;
        StartWindow(now, tickManager.TicksAbs);
    }

    private static void StartWindow(float now, int tick)
    {
        _windowStart = now;
        _windowStartTick = tick;
        _idleTime = 0f;
        _lastFrame = now;
    }
}
