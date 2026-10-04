using System;
using System.Collections.Generic;
using System.Linq;

namespace RimMult.Shared.Time;

/// <summary>Result of one coordination step: how far everyone may simulate and at what speed.</summary>
public readonly struct TickGrantInfo
{
    public TickGrantInfo(long horizonTick, GameSpeed speed, int bottleneckPlayerId, bool boost = false)
    {
        HorizonTick = horizonTick;
        Speed = speed;
        BottleneckPlayerId = bottleneckPlayerId;
        Boost = boost;
    }

    /// <summary>Superfast may run doubled (RimWorld's "nothing happening" speed-up): every authority is idle.</summary>
    public bool Boost { get; }

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
/// every authority within the allowed drift (<see cref="TimeSettings.MaxDriftTicks"/> /
/// <see cref="TimeSettings.MaxDriftSeconds"/>) of the slowest one.
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
    private readonly HashSet<int> _holds = new();
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
        // "Anyone can pause" goes both ways: choosing a speed while paused lifts the others' pause votes,
        // otherwise only the player who paused could ever resume.
        if (speed is { } chosen && chosen != GameSpeed.Paused && Settings.AnyoneCanPause)
        {
            foreach (var other in _votes.Where(v => v.Value == GameSpeed.Paused && v.Key != playerId).Select(v => v.Key).ToList())
                _votes.Remove(other);
        }

        if (speed is { } value)
            _votes[playerId] = value;
        else
            _votes.Remove(playerId);
    }

    /// <summary>How far ahead of the slowest authority the others may run at <paramref name="speed"/>.</summary>
    public long DriftTicks(GameSpeed speed, bool boost = false) => Settings.DriftTicks(speed.TicksPerSecond(boost));

    /// <param name="idle">Nothing happens in this game (everyone asleep, no threats): it could run Superfast doubled.</param>
    public void ReportAuthority(int playerId, long tick, float sustainableTicksPerSecond, bool idle = false)
    {
        _authorities[playerId] = new AuthorityState(tick, sustainableTicksPerSecond, idle);
    }

    /// <summary>
    /// RimWorld doubles Superfast while nothing happens. With one clock for everyone that may only happen when it holds
    /// in every game: one colony racing at x12 while another runs x6 would only stop and go at the horizon.
    /// </summary>
    public bool ResolveBoost(GameSpeed speed)
    {
        if (speed != GameSpeed.Superfast || _authorities.Count == 0)
            return false;
        foreach (var state in _authorities.Values)
        {
            if (!state.Idle)
                return false;
        }
        return true;
    }

    /// <summary>The player stopped simulating (left to the main menu) but may still vote.</summary>
    public void RemoveAuthority(int playerId) => _authorities.Remove(playerId);

    /// <summary>Forgets everything about a player (disconnect).</summary>
    public void RemovePlayer(int playerId)
    {
        _votes.Remove(playerId);
        _authorities.Remove(playerId);
        _holds.Remove(playerId);
    }

    /// <summary>
    /// Keeps the world paused for <paramref name="playerId"/> regardless of votes (a co-op guest loading the game:
    /// time must not run away from the copy they are loading). Unlike a pause vote, nobody else can lift it.
    /// </summary>
    public void SetHold(int playerId, bool hold)
    {
        if (hold)
            _holds.Add(playerId);
        else
            _holds.Remove(playerId);
    }

    public bool AnyHold => _holds.Count > 0;

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
        if (_holds.Count > 0)
            return GameSpeed.Paused;

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
        var boost = ResolveBoost(speed);

        var minTick = long.MaxValue;
        var maxTick = long.MinValue;
        var bottleneck = -1;
        var bottleneckRate = float.MaxValue;
        var targetRate = speed.TicksPerSecond(boost) * BottleneckThreshold;

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
        var candidate = speed == GameSpeed.Paused ? maxTick : minTick + DriftTicks(speed, boost);

        // The horizon never moves backwards: an authority may already have simulated up to the old one.
        _horizon = Math.Max(_horizon, candidate);
        return new TickGrantInfo(_horizon, speed, bottleneck, boost);
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
        public AuthorityState(long tick, float sustainableRate, bool idle)
        {
            Tick = tick;
            SustainableRate = sustainableRate;
            Idle = idle;
        }

        public long Tick { get; }
        public float SustainableRate { get; }
        public bool Idle { get; }
    }
}
