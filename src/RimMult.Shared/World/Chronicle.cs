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

    /// <summary>Actor and target signed a non-aggression pact. <see cref="ChronicleEntry.A"/>: days.</summary>
    PactMade = 15,

    /// <summary>Actor tore up its treaties with target.</summary>
    TreatyBroken = 16,

    /// <summary>Actor agreed to pay target <see cref="ChronicleEntry.A"/> silver a quadrum for <see cref="ChronicleEntry.B"/> days.</summary>
    TributeAgreed = 17,

    /// <summary>Actor paid target <see cref="ChronicleEntry.A"/> silver of tribute.</summary>
    TributePaid = 18,

    /// <summary>Actor turned down target's ultimatum: war.</summary>
    UltimatumRejected = 19,

    /// <summary>The treaty between actor and target ran its course, kept by both.</summary>
    TreatyExpired = 20,

    /// <summary>Actor shared research with target. <see cref="ChronicleEntry.Text"/>: the project; <see cref="ChronicleEntry.A"/>: points.</summary>
    ResearchShared = 21,

    /// <summary>Actor bought target's lot. <see cref="ChronicleEntry.A"/>: price; <see cref="ChronicleEntry.Text"/>: the goods.</summary>
    MarketSale = 22,

    /// <summary>Actor delivered target's order. <see cref="ChronicleEntry.A"/>: reward; <see cref="ChronicleEntry.Text"/>: the goods.</summary>
    OrderDelivered = 23,
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

    /// <summary>Reputation: wars this player declared.</summary>
    public int WarsDeclared { get; set; }

    /// <summary>Reputation: alliances broken and treaties torn up by this player.</summary>
    public int TreatiesBroken { get; set; }

    /// <summary>Reputation: pacts and tributes this player saw through to the end.</summary>
    public int TreatiesKept { get; set; }

    /// <summary>Silver paid as tribute, in total.</summary>
    public int TributePaid { get; set; }

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
        writer.WriteVarInt(WarsDeclared);
        writer.WriteVarInt(TreatiesBroken);
        writer.WriteVarInt(TreatiesKept);
        writer.WriteVarInt(TributePaid);
    }

    /// <param name="withReputation">False for world files older than format 7, which had no reputation.</param>
    public static PlayerStats Read(ByteReader reader, bool withReputation = true)
    {
        var stats = ReadBase(reader);
        if (withReputation)
        {
            stats.WarsDeclared = (int)reader.ReadVarInt();
            stats.TreatiesBroken = (int)reader.ReadVarInt();
            stats.TreatiesKept = (int)reader.ReadVarInt();
            stats.TributePaid = (int)reader.ReadVarInt();
        }
        return stats;
    }

    private static PlayerStats ReadBase(ByteReader reader) => new()
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

    public static List<PlayerStats> ReadList(ByteReader reader, bool withReputation = true)
    {
        var count = reader.ReadVarUInt();
        if (count > 100_000)
            throw new ProtocolException($"Too many players: {count}");
        var stats = new List<PlayerStats>((int)count);
        for (var i = 0UL; i < count; i++)
            stats.Add(Read(reader, withReputation));
        return stats;
    }
}

public static class Chronicle
{
    /// <summary>The world keeps this many entries; the oldest go first.</summary>
    public const int MaxEntries = 1000;
}
