using System;
using System.Collections.Generic;

namespace RimMult.Shared.Time;

/// <summary>Result of one coordination step: how far everyone may simulate and at what speed.</summary>
public readonly struct TickGrantInfo
{
    public TickGrantInfo(long horizonTick, GameSpeed speed, int bottleneckPlayerId)
    {
        HorizonTick = horizonTick;
        Speed = speed;
        BottleneckPlayerId = bottleneckPlayerId;
    }

    /// <summary>No authority may simulate past this tick.</summary>
    public long HorizonTick { get; }

    public GameSpeed Speed { get; }

    /// <summary>
    /// Player whose machine cannot sustain <see cref="Speed"/> and therefore slows the whole world,
    /// or -1. The UI uses this to suggest moving that player's simulation to a stronger machine.
    /// </summary>
    public int BottleneckPlayerId { get; }
}

/// <summary>
/// Keeps one shared clock for a world whose maps are simulated on different machines.
/// Each simulation authority reports its current tick and how many ticks per second it can sustain;
/// players vote on speed. The coordinator resolves the vote and hands out a tick horizon that keeps
/// every authority within <see cref="TimeSettings.MaxDriftTicks"/> of the slowest one.
/// Pure logic, no I/O: runs inside the in-game host and the dedicated server alike.
/// </summary>
public sealed class TimeCoordinator
{
    /// <summary>
    /// An authority counts as a bottleneck only if it reaches less than this share of the target rate,
    /// so ordinary frame-time jitter does not flag anyone.
    /// </summary>
    public const float BottleneckThreshold = 0.9f;

    private readonly Dictionary<int, GameSpeed> _votes = new();
    private readonly Dictionary<int, AuthorityState> _authorities = new();
    private long _horizon;

    public TimeCoordinator(TimeSettings settings)
    {
        Settings = settings;
    }

    public TimeSettings Settings { get; }

    /// <summary>Player whose vote decides in <see cref="SpeedVoteMode.Host"/> mode.</summary>
    public int HostPlayerId { get; set; } = -1;

    /// <summary>Records a vote; <c>null</c> withdraws it (the player abstains).</summary>
    public void SetVote(int playerId, GameSpeed? speed)
    {
        if (speed is { } value)
            _votes[playerId] = value;
        else
            _votes.Remove(playerId);
    }

    public void ReportAuthority(int playerId, long tick, float sustainableTicksPerSecond)
    {
        _authorities[playerId] = new AuthorityState(tick, sustainableTicksPerSecond);
    }

    /// <summary>The player stopped simulating (left to the main menu) but may still vote.</summary>
    public void RemoveAuthority(int playerId) => _authorities.Remove(playerId);

    /// <summary>Forgets everything about a player (disconnect).</summary>
    public void RemovePlayer(int playerId)
    {
        _votes.Remove(playerId);
        _authorities.Remove(playerId);
    }

    public bool HasAuthorities => _authorities.Count > 0;

    /// <summary>Tick of the authority furthest behind: where the world as a whole is. Null without authorities.</summary>
    public long? SlowestTick
    {
        get
        {
            if (_authorities.Count == 0)
                return null;
            var min = long.MaxValue;
            foreach (var state in _authorities.Values)
                min = Math.Min(min, state.Tick);
            return min;
        }
    }

    public GameSpeed ResolveSpeed()
    {
        // Nobody has expressed an opinion yet (fresh world, everyone loading): stay paused.
        if (_votes.Count == 0)
            return GameSpeed.Paused;

        if (Settings.AnyoneCanPause && _votes.ContainsValue(GameSpeed.Paused))
            return GameSpeed.Paused;

        var resolved = Settings.VoteMode switch
        {
            SpeedVoteMode.Lowest => Lowest(),
            SpeedVoteMode.Majority => Majority(),
            SpeedVoteMode.Host => _votes.TryGetValue(HostPlayerId, out var hostVote) ? hostVote : GameSpeed.Paused,
            _ => throw new InvalidOperationException($"Unknown vote mode {Settings.VoteMode}"),
        };
        return resolved > Settings.MaxSpeed ? Settings.MaxSpeed : resolved;
    }

    public TickGrantInfo ComputeGrant()
    {
        var speed = ResolveSpeed();
        if (_authorities.Count == 0)
            return new TickGrantInfo(_horizon, speed, -1);

        var minTick = long.MaxValue;
        var maxTick = long.MinValue;
        var bottleneck = -1;
        var bottleneckRate = float.MaxValue;
        var targetRate = speed.TicksPerSecond() * BottleneckThreshold;

        foreach (var pair in _authorities)
        {
            var state = pair.Value;
            minTick = Math.Min(minTick, state.Tick);
            maxTick = Math.Max(maxTick, state.Tick);

            if (state.SustainableRate < targetRate && state.SustainableRate < bottleneckRate)
            {
                bottleneck = pair.Key;
                bottleneckRate = state.SustainableRate;
            }
        }

        // While paused, let laggards catch up to the leader so everyone resumes from the same tick.
        var candidate = speed == GameSpeed.Paused ? maxTick : minTick + Settings.MaxDriftTicks;

        // The horizon never moves backwards: an authority may already have simulated up to the old one.
        _horizon = Math.Max(_horizon, candidate);
        return new TickGrantInfo(_horizon, speed, bottleneck);
    }

    private GameSpeed Lowest()
    {
        var lowest = GameSpeed.Ultrafast;
        foreach (var vote in _votes.Values)
        {
            if (vote < lowest)
                lowest = vote;
        }
        return lowest;
    }

    private GameSpeed Majority()
    {
        var counts = new int[(int)GameSpeed.Ultrafast + 1];
        foreach (var vote in _votes.Values)
            counts[(int)vote]++;

        // Iterate from slowest to fastest and only replace on a strictly higher count: ties go to the slower speed.
        var best = GameSpeed.Paused;
        for (var i = 0; i < counts.Length; i++)
        {
            if (counts[i] > counts[(int)best])
                best = (GameSpeed)i;
        }
        return best;
    }

    private readonly struct AuthorityState
    {
        public AuthorityState(long tick, float sustainableRate)
        {
            Tick = tick;
            SustainableRate = sustainableRate;
        }

        public long Tick { get; }
        public float SustainableRate { get; }
    }
}
