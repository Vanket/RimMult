using RimMult.Shared.Serialization;
using RimMult.Shared.Time;

namespace RimMult.Shared.Packets;

/// <summary>Client → server: the speed this player wants, or <c>null</c> to abstain.</summary>
public sealed class SpeedVote : IPacket
{
    private const byte Abstain = 0xFF;

    public GameSpeed? Speed { get; set; }

    public PacketType Type => PacketType.SpeedVote;

    public void Write(ByteWriter writer) => writer.WriteByte(Speed is { } speed ? (byte)speed : Abstain);

    public static SpeedVote Read(ByteReader reader)
    {
        var raw = reader.ReadByte();
        if (raw == Abstain)
            return new SpeedVote();

        var speed = (GameSpeed)raw;
        if (!speed.IsDefined())
            throw new ProtocolException($"Invalid speed {raw}");
        return new SpeedVote { Speed = speed };
    }
}

/// <summary>
/// Client → server, several times a second from every simulation authority:
/// where its simulation is and how fast it could go if nothing held it back.
/// </summary>
public sealed class AuthorityReport : IPacket
{
    public long Tick { get; set; }

    /// <summary>Measured ticks per second the machine sustains for everything it simulates.</summary>
    public float SustainableTicksPerSecond { get; set; }

    /// <summary>Nothing happens in this game (everyone asleep, no threats): it could run Superfast doubled.</summary>
    public bool Idle { get; set; }

    public PacketType Type => PacketType.AuthorityReport;

    public void Write(ByteWriter writer)
    {
        writer.WriteVarInt(Tick);
        writer.WriteFloat(SustainableTicksPerSecond);
        writer.WriteBool(Idle);
    }

    public static AuthorityReport Read(ByteReader reader)
    {
        var report = new AuthorityReport
        {
            Tick = reader.ReadVarInt(),
            SustainableTicksPerSecond = reader.ReadFloat(),
            Idle = reader.ReadBool(),
        };
        if (report.Tick < 0 || float.IsNaN(report.SustainableTicksPerSecond) || report.SustainableTicksPerSecond < 0)
            throw new ProtocolException("Invalid authority report");
        return report;
    }
}

/// <summary>Server → clients: the shared clock. Superseded by the next one, so it may be sent unreliably.</summary>
public sealed class TickGrant : IPacket
{
    public long HorizonTick { get; set; }
    public GameSpeed Speed { get; set; }
    public int BottleneckPlayerId { get; set; } = -1;

    /// <summary>Superfast may run doubled: nothing happens in any game in the world.</summary>
    public bool Boost { get; set; }

    public PacketType Type => PacketType.TickGrant;

    public void Write(ByteWriter writer)
    {
        writer.WriteVarInt(HorizonTick);
        writer.WriteByte((byte)Speed);
        writer.WriteVarInt(BottleneckPlayerId);
        writer.WriteBool(Boost);
    }

    public static TickGrant Read(ByteReader reader)
    {
        var grant = new TickGrant
        {
            HorizonTick = reader.ReadVarInt(),
            Speed = (GameSpeed)reader.ReadByte(),
            BottleneckPlayerId = (int)reader.ReadVarInt(),
            Boost = reader.ReadBool(),
        };
        if (!grant.Speed.IsDefined())
            throw new ProtocolException("Invalid speed in tick grant");
        return grant;
    }

    public static TickGrant From(TickGrantInfo info) => new()
    {
        HorizonTick = info.HorizonTick,
        Speed = info.Speed,
        BottleneckPlayerId = info.BottleneckPlayerId,
        Boost = info.Boost,
    };
}
