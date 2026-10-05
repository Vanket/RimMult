using System;
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

    // The broker's game plays the NPC factions (see MarketState.BrokerPlayerId); the server takes these only from it.

    /// <summary>An NPC faction puts goods up (for <see cref="MarketAction.Days"/> days).</summary>
    NpcPostLot = 7,

    /// <summary>An NPC faction orders goods, the reward's silver in the payload.</summary>
    NpcPostOrder = 8,

    /// <summary>An NPC faction buys a player's lot: the payload is the silver.</summary>
    NpcBuyLot = 9,

    /// <summary>An NPC faction delivers a player's order: the payload is the goods.</summary>
    NpcFulfillOrder = 10,
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

    /// <summary>Game days an order (or an NPC lot) stays open.</summary>
    public int Days { get; set; }

    /// <summary>Silver paid as the market's fee (burnt by the game that pays it; the server checks the sum).</summary>
    public int Fee { get; set; }

    /// <summary>NPC actions: the faction (def, which one of that def, its name).</summary>
    public string NpcFaction { get; set; } = "";
    public int NpcFactionIndex { get; set; }
    public string NpcName { get; set; } = "";

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
        writer.WriteVarInt(Fee);
        writer.WriteString(NpcFaction);
        writer.WriteVarInt(NpcFactionIndex);
        writer.WriteString(NpcName);
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
            Fee = (int)reader.ReadVarInt(),
            NpcFaction = reader.ReadRequiredString(),
            NpcFactionIndex = (int)reader.ReadVarInt(),
            NpcName = reader.ReadRequiredString(),
            Payload = reader.ReadBytes(),
        };
        if (action.Kind < MarketActionKind.PostLot || action.Kind > MarketActionKind.NpcFulfillOrder)
            throw new ProtocolException($"Unknown market action {(byte)action.Kind}");
        if (action.Payload.Length > MailItem.MaxPayloadBytes)
            throw new ProtocolException("Market goods too large");
        return action;
    }
}

/// <summary>
/// Server → client: the market's lots and orders (without the goods), its rules and the world's prices. Sent on
/// joining and whenever it changes.
/// </summary>
public sealed class MarketState : IPacket
{
    public List<MarketLot> Lots { get; set; } = new();
    public List<MarketOrder> Orders { get; set; } = new();

    /// <summary>The player whose game plays the NPC factions now (-1: nobody, or NPCs are off).</summary>
    public int BrokerPlayerId { get; set; } = -1;

    /// <summary>NPC limits: lots and orders they keep on the market at most.</summary>
    public int NpcMaxLots { get; set; }
    public int NpcMaxOrders { get; set; }

    /// <summary>Lots and orders a player may have open for free; each one beyond costs <see cref="FeePercent"/> of its price.</summary>
    public int FreeListings { get; set; }
    public int FeePercent { get; set; }

    public MarketPrices Prices { get; set; } = new();

    public PacketType Type => PacketType.MarketState;

    public void Write(ByteWriter writer)
    {
        MarketLot.WriteList(writer, Lots, withPayload: false);
        MarketOrder.WriteList(writer, Orders, withPayload: false);
        writer.WriteVarInt(BrokerPlayerId);
        writer.WriteVarInt(NpcMaxLots);
        writer.WriteVarInt(NpcMaxOrders);
        writer.WriteVarInt(FreeListings);
        writer.WriteVarInt(FeePercent);
        Prices.Write(writer);
    }

    public static MarketState Read(ByteReader reader) => new()
    {
        Lots = MarketLot.ReadList(reader, withPayload: false),
        Orders = MarketOrder.ReadList(reader, withPayload: false),
        BrokerPlayerId = (int)reader.ReadVarInt(),
        NpcMaxLots = (int)reader.ReadVarInt(),
        NpcMaxOrders = (int)reader.ReadVarInt(),
        FreeListings = (int)reader.ReadVarInt(),
        FeePercent = (int)reader.ReadVarInt(),
        Prices = MarketPrices.Read(reader),
    };

    /// <summary>The fee for one more listing by a player who has <paramref name="open"/> lots and orders open now.</summary>
    public static int FeeFor(int price, int open, int freeListings, int feePercent) =>
        open < freeListings || feePercent <= 0 ? 0 : (int)Math.Ceiling(price * feePercent / 100.0);
}
