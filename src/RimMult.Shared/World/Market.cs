using System;
using System.Collections.Generic;
using RimMult.Shared.Serialization;

namespace RimMult.Shared.World;

/// <summary>
/// Goods put up for sale on the world's market. The server holds them (packed, opaque to it) until someone buys them
/// or the seller takes them back, so a sale happens even while the seller is away.
/// </summary>
public sealed class MarketLot
{
    public const int MaxPerPlayer = 20;
    public const int MaxPrice = 1_000_000;

    public long Id { get; set; }
    public ulong Seller { get; set; }
    public string SellerName { get; set; } = "";

    /// <summary>"Steel x200, Medicine x10".</summary>
    public string Summary { get; set; } = "";

    /// <summary>Market value of the goods in the seller's game (a hint for buyers).</summary>
    public float Value { get; set; }

    /// <summary>Silver asked.</summary>
    public int Price { get; set; }

    public long PostedTick { get; set; }

    /// <summary>The goods (server and world file only; not sent with the market's listing).</summary>
    public byte[] Payload { get; set; } = Array.Empty<byte>();

    public void Write(ByteWriter writer, bool withPayload)
    {
        writer.WriteVarInt(Id);
        writer.WriteUInt64(Seller);
        writer.WriteString(SellerName);
        writer.WriteString(Summary);
        writer.WriteFloat(Value);
        writer.WriteVarInt(Price);
        writer.WriteVarInt(PostedTick);
        if (withPayload)
            writer.WriteBytes(Payload);
    }

    public static MarketLot Read(ByteReader reader, bool withPayload) => new()
    {
        Id = reader.ReadVarInt(),
        Seller = reader.ReadUInt64(),
        SellerName = reader.ReadRequiredString(),
        Summary = reader.ReadRequiredString(),
        Value = reader.ReadFloat(),
        Price = (int)reader.ReadVarInt(),
        PostedTick = reader.ReadVarInt(),
        Payload = withPayload ? reader.ReadBytes() : Array.Empty<byte>(),
    };

    public static void WriteList(ByteWriter writer, IReadOnlyList<MarketLot> lots, bool withPayload)
    {
        writer.WriteVarUInt((ulong)lots.Count);
        foreach (var lot in lots)
            lot.Write(writer, withPayload);
    }

    public static List<MarketLot> ReadList(ByteReader reader, bool withPayload)
    {
        var count = reader.ReadVarUInt();
        if (count > 100_000)
            throw new ProtocolException($"Too many lots: {count}");
        var lots = new List<MarketLot>((int)count);
        for (var i = 0UL; i < count; i++)
            lots.Add(Read(reader, withPayload));
        return lots;
    }
}

/// <summary>
/// "I need 10 medicine by the 15th, I pay 400": the reward is held by the server until someone delivers the goods (or
/// the deadline passes, or the buyer calls it off — then it goes back).
/// </summary>
public sealed class MarketOrder
{
    public const int MaxPerPlayer = 20;
    public const int MaxCount = 100_000;

    public long Id { get; set; }
    public ulong Buyer { get; set; }
    public string BuyerName { get; set; } = "";

    /// <summary>The item wanted (ThingDef name) and its label in the buyer's game.</summary>
    public string DefName { get; set; } = "";
    public string Label { get; set; } = "";
    public int Count { get; set; }

    /// <summary>Silver paid to whoever delivers.</summary>
    public int Reward { get; set; }

    public long PostedTick { get; set; }

    /// <summary>World tick by which it must be delivered.</summary>
    public long DeadlineTick { get; set; }

    /// <summary>The reward's silver (server and world file only).</summary>
    public byte[] Payload { get; set; } = Array.Empty<byte>();

    public void Write(ByteWriter writer, bool withPayload)
    {
        writer.WriteVarInt(Id);
        writer.WriteUInt64(Buyer);
        writer.WriteString(BuyerName);
        writer.WriteString(DefName);
        writer.WriteString(Label);
        writer.WriteVarInt(Count);
        writer.WriteVarInt(Reward);
        writer.WriteVarInt(PostedTick);
        writer.WriteVarInt(DeadlineTick);
        if (withPayload)
            writer.WriteBytes(Payload);
    }

    public static MarketOrder Read(ByteReader reader, bool withPayload) => new()
    {
        Id = reader.ReadVarInt(),
        Buyer = reader.ReadUInt64(),
        BuyerName = reader.ReadRequiredString(),
        DefName = reader.ReadRequiredString(),
        Label = reader.ReadRequiredString(),
        Count = (int)reader.ReadVarInt(),
        Reward = (int)reader.ReadVarInt(),
        PostedTick = reader.ReadVarInt(),
        DeadlineTick = reader.ReadVarInt(),
        Payload = withPayload ? reader.ReadBytes() : Array.Empty<byte>(),
    };

    public static void WriteList(ByteWriter writer, IReadOnlyList<MarketOrder> orders, bool withPayload)
    {
        writer.WriteVarUInt((ulong)orders.Count);
        foreach (var order in orders)
            order.Write(writer, withPayload);
    }

    public static List<MarketOrder> ReadList(ByteReader reader, bool withPayload)
    {
        var count = reader.ReadVarUInt();
        if (count > 100_000)
            throw new ProtocolException($"Too many orders: {count}");
        var orders = new List<MarketOrder>((int)count);
        for (var i = 0UL; i < count; i++)
            orders.Add(Read(reader, withPayload));
        return orders;
    }
}
