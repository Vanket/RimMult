using RimMult.Shared.Serialization;

namespace RimMult.Shared.Time;

public enum SpeedVoteMode : byte
{
    /// <summary>The slowest vote wins. Default: nobody gets dragged faster than they want.</summary>
    Lowest = 0,

    /// <summary>The most common vote wins; ties go to the slower speed.</summary>
    Majority = 1,

    /// <summary>Only the host's vote counts.</summary>
    Host = 2,
}

public sealed class TimeSettings
{
    public SpeedVoteMode VoteMode { get; set; } = SpeedVoteMode.Lowest;

    /// <summary>
    /// When true a single "pause" vote pauses the world regardless of <see cref="VoteMode"/>, and likewise any
    /// player can lift it: choosing a speed while the world is paused clears everyone's pause votes.
    /// </summary>
    public bool AnyoneCanPause { get; set; } = true;

    /// <summary>Upper bound on the resolved speed. Ultrafast is dev-mode only in vanilla.</summary>
    public GameSpeed MaxSpeed { get; set; } = GameSpeed.Superfast;

    /// <summary>
    /// How far (in ticks) the fastest simulation authority may run ahead of the slowest one.
    /// 60 ticks = one in-game second at normal speed: invisible to players, but enough slack to absorb hitches.
    /// </summary>
    public int MaxDriftTicks { get; set; } = 60;

    /// <summary>
    /// The same slack in real seconds, so it scales with speed (1.5 s = 540 ticks at speed 3). The larger of the
    /// two applies. A fixed tick budget is shorter than network/report delay at high speed, which makes colonies
    /// stop and start every few frames (visible as pawns jumping).
    /// </summary>
    public float MaxDriftSeconds { get; set; } = 1.5f;

    /// <summary>How far ahead of the slowest authority the others may run, at this many ticks per second.</summary>
    public long DriftTicks(int ticksPerSecond) => System.Math.Max(MaxDriftTicks, (long)(ticksPerSecond * MaxDriftSeconds));

    public void Write(ByteWriter writer)
    {
        writer.WriteByte((byte)VoteMode);
        writer.WriteBool(AnyoneCanPause);
        writer.WriteByte((byte)MaxSpeed);
        writer.WriteVarInt(MaxDriftTicks);
        writer.WriteFloat(MaxDriftSeconds);
    }

    public static TimeSettings Read(ByteReader reader)
    {
        var settings = new TimeSettings
        {
            VoteMode = (SpeedVoteMode)reader.ReadByte(),
            AnyoneCanPause = reader.ReadBool(),
            MaxSpeed = (GameSpeed)reader.ReadByte(),
            MaxDriftTicks = (int)reader.ReadVarInt(),
            MaxDriftSeconds = reader.ReadFloat(),
        };
        if (settings.VoteMode > SpeedVoteMode.Host || !settings.MaxSpeed.IsDefined() || settings.MaxDriftTicks < 0
            || float.IsNaN(settings.MaxDriftSeconds) || settings.MaxDriftSeconds < 0)
            throw new ProtocolException("Invalid time settings");
        return settings;
    }
}
