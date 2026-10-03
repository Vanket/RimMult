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

    /// <summary>When true a single "pause" vote pauses the world regardless of <see cref="VoteMode"/>.</summary>
    public bool AnyoneCanPause { get; set; } = true;

    /// <summary>Upper bound on the resolved speed. Ultrafast is dev-mode only in vanilla.</summary>
    public GameSpeed MaxSpeed { get; set; } = GameSpeed.Superfast;

    /// <summary>
    /// How far (in ticks) the fastest simulation authority may run ahead of the slowest one.
    /// 60 ticks = one in-game second at normal speed: invisible to players, but enough slack to absorb hitches.
    /// </summary>
    public int MaxDriftTicks { get; set; } = 60;

    public void Write(ByteWriter writer)
    {
        writer.WriteByte((byte)VoteMode);
        writer.WriteBool(AnyoneCanPause);
        writer.WriteByte((byte)MaxSpeed);
        writer.WriteVarInt(MaxDriftTicks);
    }

    public static TimeSettings Read(ByteReader reader)
    {
        var settings = new TimeSettings
        {
            VoteMode = (SpeedVoteMode)reader.ReadByte(),
            AnyoneCanPause = reader.ReadBool(),
            MaxSpeed = (GameSpeed)reader.ReadByte(),
            MaxDriftTicks = (int)reader.ReadVarInt(),
        };
        if (settings.VoteMode > SpeedVoteMode.Host || !settings.MaxSpeed.IsDefined() || settings.MaxDriftTicks < 0)
            throw new ProtocolException("Invalid time settings");
        return settings;
    }
}
