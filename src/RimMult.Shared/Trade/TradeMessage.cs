using System.Collections.Generic;
using RimMult.Shared.Serialization;

namespace RimMult.Shared.Trade;

public enum TradeMessageKind : byte
{
    /// <summary>"Let's trade" — opens the negotiation on the other side once they join.</summary>
    Invite = 1,

    /// <summary>The invited player agreed to trade.</summary>
    Join = 2,

    /// <summary>Either side: the trade is off.</summary>
    Cancel = 3,

    /// <summary>The sender's offer changed: <see cref="TradeMessage.Version"/> and <see cref="TradeMessage.Lines"/>.</summary>
    Offer = 4,

    /// <summary>The sender accepts the receiver's offer at <see cref="TradeMessage.Version"/> (-1 withdraws).</summary>
    Accept = 5,

    /// <summary>
    /// Final confirmation: the sender will hand over its offer <see cref="TradeMessage.Version"/> in exchange for the
    /// receiver's offer <see cref="TradeMessage.OtherVersion"/>. Each side executes once both commits match.
    /// </summary>
    Commit = 6,
}

/// <summary>One line of an offer, for display on the other side (the actual items stay in the offerer's game).</summary>
public sealed class TradeLine
{
    public string Label { get; set; } = "";
    public int Count { get; set; }

    /// <summary>Market value of the whole line in the offerer's game.</summary>
    public float Value { get; set; }

    public void Write(ByteWriter writer)
    {
        writer.WriteString(Label);
        writer.WriteVarInt(Count);
        writer.WriteFloat(Value);
    }

    public static TradeLine Read(ByteReader reader) => new()
    {
        Label = reader.ReadRequiredString(),
        Count = (int)reader.ReadVarInt(),
        Value = reader.ReadFloat(),
    };
}

public sealed class TradeMessage
{
    public const int MaxLines = 2000;

    public TradeMessageKind Kind { get; set; }
    public int Version { get; set; }
    public int OtherVersion { get; set; }
    public List<TradeLine> Lines { get; set; } = new();

    public byte[] Encode()
    {
        var writer = new ByteWriter();
        writer.WriteByte((byte)Kind);
        writer.WriteVarInt(Version);
        writer.WriteVarInt(OtherVersion);
        writer.WriteVarUInt((ulong)Lines.Count);
        foreach (var line in Lines)
            line.Write(writer);
        return writer.ToArray();
    }

    public static TradeMessage Decode(byte[] data)
    {
        var reader = new ByteReader(data);
        var message = new TradeMessage
        {
            Kind = (TradeMessageKind)reader.ReadByte(),
            Version = (int)reader.ReadVarInt(),
            OtherVersion = (int)reader.ReadVarInt(),
        };
        if (message.Kind < TradeMessageKind.Invite || message.Kind > TradeMessageKind.Commit)
            throw new ProtocolException($"Unknown trade message {(byte)message.Kind}");
        var count = reader.ReadVarUInt();
        if (count > MaxLines)
            throw new ProtocolException($"Too many trade lines: {count}");
        for (var i = 0UL; i < count; i++)
            message.Lines.Add(TradeLine.Read(reader));
        reader.EnsureFullyRead();
        return message;
    }
}
