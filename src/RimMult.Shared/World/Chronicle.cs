using System.Collections.Generic;
using RimMult.Shared.Serialization;

namespace RimMult.Shared.World;

/// <summary>What happened in the world. Wire values: never renumber, append new ones.</summary>
public enum ChronicleKind : byte
{
    WorldCreated = 1,

    /// <summary>A player entered this world for the first time.</summary>
    PlayerArrived = 2,

    /// <summary><see cref="ChronicleEntry.Text"/>: the colony's name.</summary>
    ColonyFounded = 3,

    /// <summary><see cref="ChronicleEntry.Text"/>: the colony's name.</summary>
    ColonyAbandoned = 4,

    WarDeclared = 5,
    PeaceMade = 6,
    AllianceMade = 7,
    AllianceBroken = 8,

    /// <summary>Actor raids target. <see cref="ChronicleEntry.A"/>: 1 when led in person; <see cref="ChronicleEntry.Text"/>: the raiders.</summary>
    RaidLaunched = 9,

    /// <summary>
    /// Actor's raid on target is over. <see cref="ChronicleEntry.A"/> came back, <see cref="ChronicleEntry.B"/> captives taken,
    /// <see cref="ChronicleEntry.C"/> died.
    /// </summary>
    RaidEnded = 10,

    /// <summary>Actor's people went to help target in person.</summary>
    HelpSent = 11,

    /// <summary>Actor's helpers are back from target. <see cref="ChronicleEntry.C"/> died.</summary>
    HelpEnded = 12,

    /// <summary>Goods (or people moving over) sent from actor to target. <see cref="ChronicleEntry.Text"/>: what.</summary>
    ParcelSent = 13,

    /// <summary>Actor wiped an NPC settlement off the map. <see cref="ChronicleEntry.Text"/>: its tile.</summary>
    SettlementDestroyed = 14,
}

/// <summary>One line of the world's chronicle. Names are kept as they were, so the line reads the same later.</summary>
public sealed class ChronicleEntry
{
    /// <summary>World time (RimWorld absolute ticks) when it happened.</summary>
    public long Tick { get; set; }

    /// <summary>Real time (Unix seconds, UTC).</summary>
    public long UnixTime { get; set; }

    public ChronicleKind Kind { get; set; }
    public ulong Actor { get; set; }
    public string ActorName { get; set; } = "";

    /// <summary>The other player, or 0.</summary>
    public ulong Target { get; set; }
    public string TargetName { get; set; } = "";

    /// <summary>Numbers whose meaning depends on <see cref="Kind"/>.</summary>
    public int A { get; set; }
    public int B { get; set; }
    public int C { get; set; }
    public string Text { get; set; } = "";

    public void Write(ByteWriter writer)
    {
        writer.WriteVarInt(Tick);
        writer.WriteVarInt(UnixTime);
        writer.WriteByte((byte)Kind);
        writer.WriteUInt64(Actor);
        writer.WriteString(ActorName);
        writer.WriteUInt64(Target);
        writer.WriteString(TargetName);
        writer.WriteVarInt(A);
        writer.WriteVarInt(B);
        writer.WriteVarInt(C);
        writer.WriteString(Text);
    }

    public static ChronicleEntry Read(ByteReader reader) => new()
    {
        Tick = reader.ReadVarInt(),
        UnixTime = reader.ReadVarInt(),
        Kind = (ChronicleKind)reader.ReadByte(),
        Actor = reader.ReadUInt64(),
        ActorName = reader.ReadRequiredString(),
        Target = reader.ReadUInt64(),
        TargetName = reader.ReadRequiredString(),
        A = (int)reader.ReadVarInt(),
        B = (int)reader.ReadVarInt(),
        C = (int)reader.ReadVarInt(),
        Text = reader.ReadRequiredString(),
    };

    public static void WriteList(ByteWriter writer, IReadOnlyList<ChronicleEntry> entries)
    {
        writer.WriteVarUInt((ulong)entries.Count);
        foreach (var entry in entries)
            entry.Write(writer);
    }

    public static List<ChronicleEntry> ReadList(ByteReader reader)
    {
        var count = reader.ReadVarUInt();
        if (count > (ulong)Chronicle.MaxEntries)
            throw new ProtocolException($"Too many chronicle entries: {count}");
        var entries = new List<ChronicleEntry>((int)count);
        for (var i = 0UL; i < count; i++)
            entries.Add(Read(reader));
        return entries;
    }
}

/// <summary>A player's standing in the world, for the chronicle's table.</summary>
public sealed class PlayerStats
{
    public ulong Owner { get; set; }
    public string Name { get; set; } = "";

    /// <summary>Filled in when sent (from the world's colors); not stored.</summary>
    public byte ColorIndex { get; set; }

    /// <summary>Filled in when sent (from the world's colonies); not stored.</summary>
    public int Colonies { get; set; }

    /// <summary>Reported by the player's game (wealth of the home maps, free colonists).</summary>
    public float Wealth { get; set; }
    public int Colonists { get; set; }

    public int RaidsLed { get; set; }
    public int RaidsSuffered { get; set; }
    public int HelpsSent { get; set; }
    public int ParcelsSent { get; set; }
    public int SettlementsDestroyed { get; set; }

    public PlayerStats Copy() => (PlayerStats)MemberwiseClone();

    public void Write(ByteWriter writer)
    {
        writer.WriteUInt64(Owner);
        writer.WriteString(Name);
        writer.WriteByte(ColorIndex);
        writer.WriteVarInt(Colonies);
        writer.WriteFloat(Wealth);
        writer.WriteVarInt(Colonists);
        writer.WriteVarInt(RaidsLed);
        writer.WriteVarInt(RaidsSuffered);
        writer.WriteVarInt(HelpsSent);
        writer.WriteVarInt(ParcelsSent);
        writer.WriteVarInt(SettlementsDestroyed);
    }

    public static PlayerStats Read(ByteReader reader) => new()
    {
        Owner = reader.ReadUInt64(),
        Name = reader.ReadRequiredString(),
        ColorIndex = reader.ReadByte(),
        Colonies = (int)reader.ReadVarInt(),
        Wealth = reader.ReadFloat(),
        Colonists = (int)reader.ReadVarInt(),
        RaidsLed = (int)reader.ReadVarInt(),
        RaidsSuffered = (int)reader.ReadVarInt(),
        HelpsSent = (int)reader.ReadVarInt(),
        ParcelsSent = (int)reader.ReadVarInt(),
        SettlementsDestroyed = (int)reader.ReadVarInt(),
    };

    public static void WriteList(ByteWriter writer, IReadOnlyList<PlayerStats> stats)
    {
        writer.WriteVarUInt((ulong)stats.Count);
        foreach (var entry in stats)
            entry.Write(writer);
    }

    public static List<PlayerStats> ReadList(ByteReader reader)
    {
        var count = reader.ReadVarUInt();
        if (count > 100_000)
            throw new ProtocolException($"Too many players: {count}");
        var stats = new List<PlayerStats>((int)count);
        for (var i = 0UL; i < count; i++)
            stats.Add(Read(reader));
        return stats;
    }
}

public static class Chronicle
{
    /// <summary>The world keeps this many entries; the oldest go first.</summary>
    public const int MaxEntries = 1000;
}
