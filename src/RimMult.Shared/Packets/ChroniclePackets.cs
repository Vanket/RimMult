using System.Collections.Generic;
using RimMult.Shared.Serialization;
using RimMult.Shared.World;

namespace RimMult.Shared.Packets;

/// <summary>
/// Server → client: the world's chronicle. <see cref="Full"/>: everything (on join), replacing what the client had;
/// otherwise new entries to append. <see cref="Stats"/> always comes complete.
/// </summary>
public sealed class ChronicleUpdate : IPacket
{
    public bool Full { get; set; }
    public List<ChronicleEntry> Entries { get; set; } = new();
    public List<PlayerStats> Stats { get; set; } = new();

    public PacketType Type => PacketType.ChronicleUpdate;

    public void Write(ByteWriter writer)
    {
        writer.WriteBool(Full);
        ChronicleEntry.WriteList(writer, Entries);
        PlayerStats.WriteList(writer, Stats);
    }

    public static ChronicleUpdate Read(ByteReader reader) => new()
    {
        Full = reader.ReadBool(),
        Entries = ChronicleEntry.ReadList(reader),
        Stats = PlayerStats.ReadList(reader),
    };
}

/// <summary>Client → server, now and then while in the world: how this player's colonies are doing.</summary>
public sealed class ColonyStatsReport : IPacket
{
    /// <summary>Total wealth of the player's home maps.</summary>
    public float Wealth { get; set; }

    /// <summary>Free colonists, at home and travelling.</summary>
    public int Colonists { get; set; }

    public PacketType Type => PacketType.ColonyStatsReport;

    public void Write(ByteWriter writer)
    {
        writer.WriteFloat(Wealth);
        writer.WriteVarInt(Colonists);
    }

    public static ColonyStatsReport Read(ByteReader reader)
    {
        var report = new ColonyStatsReport { Wealth = reader.ReadFloat(), Colonists = (int)reader.ReadVarInt() };
        if (float.IsNaN(report.Wealth) || float.IsInfinity(report.Wealth) || report.Wealth < 0 || report.Colonists < 0)
            throw new ProtocolException("Invalid colony stats");
        return report;
    }
}
