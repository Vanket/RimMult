using System.Collections.Generic;
using RimMult.Shared.Serialization;
using RimMult.Shared.World;

namespace RimMult.Shared.Packets;

/// <summary>Wire values: never renumber, append new ones.</summary>
public enum MarketActionKind : byte
{
    /// <summary>Put goods (<see cref="MarketAction.Payload"/>) up for <see cref="MarketAction.Price"/> silver.</summary>
    PostLot = 1,

    /// <summary>Take a lot of one's own back (the goods come home as a parcel).</summary>
    CancelLot = 2,

    /// <summary>Buy lot <see cref="MarketAction.Id"/>: <see cref="MarketAction.Payload"/> is the silver, <see cref="MarketAction.Price"/> what it was listed at.</summary>
    BuyLot = 3,

    /// <summary>Ask for <see cref="MarketAction.Count"/> of <see cref="MarketAction.DefName"/>, paying the silver in <see cref="MarketAction.Payload"/>.</summary>
    PostOrder = 4,

    /// <summary>Call one's own order off (the reward comes back).</summary>
    CancelOrder = 5,

    /// <summary>Deliver order <see cref="MarketAction.Id"/>: <see cref="MarketAction.Payload"/> is the goods.</summary>
    FulfillOrder = 6,
}

/// <summary>Client → server: something done on the world's market.</summary>
public sealed class MarketAction : IPacket
{
    public MarketActionKind Kind { get; set; }

    /// <summary>The lot or order concerned.</summary>
    public long Id { get; set; }

    /// <summary>Lot price, or order reward (silver).</summary>
    public int Price { get; set; }

    /// <summary>Market value of a lot's goods in the seller's game.</summary>
    public float Value { get; set; }

    public int Count { get; set; }
    public string DefName { get; set; } = "";
    public string Label { get; set; } = "";
    public string Summary { get; set; } = "";

    /// <summary>Game days an order stays open.</summary>
    public int Days { get; set; }

    /// <summary>Goods or silver, packed by the game.</summary>
    public byte[] Payload { get; set; } = System.Array.Empty<byte>();

    public PacketType Type => PacketType.MarketAction;

    public void Write(ByteWriter writer)
    {
        writer.WriteByte((byte)Kind);
        writer.WriteVarInt(Id);
        writer.WriteVarInt(Price);
        writer.WriteFloat(Value);
        writer.WriteVarInt(Count);
        writer.WriteString(DefName);
        writer.WriteString(Label);
        writer.WriteString(Summary);
        writer.WriteVarInt(Days);
        writer.WriteBytes(Payload);
    }

    public static MarketAction Read(ByteReader reader)
    {
        var action = new MarketAction
        {
            Kind = (MarketActionKind)reader.ReadByte(),
            Id = reader.ReadVarInt(),
            Price = (int)reader.ReadVarInt(),
            Value = reader.ReadFloat(),
            Count = (int)reader.ReadVarInt(),
            DefName = reader.ReadRequiredString(),
            Label = reader.ReadRequiredString(),
            Summary = reader.ReadRequiredString(),
            Days = (int)reader.ReadVarInt(),
            Payload = reader.ReadBytes(),
        };
        if (action.Kind < MarketActionKind.PostLot || action.Kind > MarketActionKind.FulfillOrder)
            throw new ProtocolException($"Unknown market action {(byte)action.Kind}");
        if (action.Payload.Length > MailItem.MaxPayloadBytes)
            throw new ProtocolException("Market goods too large");
        return action;
    }
}

/// <summary>Server → client: the market's lots and orders (without the goods). Sent on joining and whenever it changes.</summary>
public sealed class MarketState : IPacket
{
    public List<MarketLot> Lots { get; set; } = new();
    public List<MarketOrder> Orders { get; set; } = new();

    public PacketType Type => PacketType.MarketState;

    public void Write(ByteWriter writer)
    {
        MarketLot.WriteList(writer, Lots, withPayload: false);
        MarketOrder.WriteList(writer, Orders, withPayload: false);
    }

    public static MarketState Read(ByteReader reader) => new()
    {
        Lots = MarketLot.ReadList(reader, withPayload: false),
        Orders = MarketOrder.ReadList(reader, withPayload: false),
    };
}
