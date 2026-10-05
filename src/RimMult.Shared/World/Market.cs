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

    /// <summary>The main item (the most valuable stack's def name): what the world's prices count it as.</summary>
    public string DefName { get; set; } = "";

    /// <summary>When the lot leaves the market by itself (NPC lots), or 0 for never.</summary>
    public long ExpiresTick { get; set; }

    /// <summary>An NPC faction's lot: its def and which one of that def (see <see cref="NpcTrader"/>); "" for a player's.</summary>
    public string NpcFaction { get; set; } = "";
    public int NpcFactionIndex { get; set; }

    public bool IsNpc => NpcFaction.Length > 0;

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
        writer.WriteString(DefName);
        writer.WriteVarInt(ExpiresTick);
        writer.WriteString(NpcFaction);
        writer.WriteVarInt(NpcFactionIndex);
        if (withPayload)
            writer.WriteBytes(Payload);
    }

    /// <param name="extended">False for world files older than format 9 (no item, expiry or NPC fields).</param>
    public static MarketLot Read(ByteReader reader, bool withPayload, bool extended = true)
    {
        var lot = new MarketLot
        {
            Id = reader.ReadVarInt(),
            Seller = reader.ReadUInt64(),
            SellerName = reader.ReadRequiredString(),
            Summary = reader.ReadRequiredString(),
            Value = reader.ReadFloat(),
            Price = (int)reader.ReadVarInt(),
            PostedTick = reader.ReadVarInt(),
        };
        if (extended)
        {
            lot.DefName = reader.ReadRequiredString();
            lot.ExpiresTick = reader.ReadVarInt();
            lot.NpcFaction = reader.ReadRequiredString();
            lot.NpcFactionIndex = (int)reader.ReadVarInt();
        }
        lot.Payload = withPayload ? reader.ReadBytes() : Array.Empty<byte>();
        return lot;
    }

    public static void WriteList(ByteWriter writer, IReadOnlyList<MarketLot> lots, bool withPayload)
    {
        writer.WriteVarUInt((ulong)lots.Count);
        foreach (var lot in lots)
            lot.Write(writer, withPayload);
    }

    public static List<MarketLot> ReadList(ByteReader reader, bool withPayload, bool extended = true)
    {
        var count = reader.ReadVarUInt();
        if (count > 100_000)
            throw new ProtocolException($"Too many lots: {count}");
        var lots = new List<MarketLot>((int)count);
        for (var i = 0UL; i < count; i++)
            lots.Add(Read(reader, withPayload, extended));
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

    /// <summary>An NPC faction's order: its def and which one of that def; "" for a player's.</summary>
    public string NpcFaction { get; set; } = "";
    public int NpcFactionIndex { get; set; }

    public bool IsNpc => NpcFaction.Length > 0;

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
        writer.WriteString(NpcFaction);
        writer.WriteVarInt(NpcFactionIndex);
        if (withPayload)
            writer.WriteBytes(Payload);
    }

    /// <param name="extended">False for world files older than format 9 (no NPC fields).</param>
    public static MarketOrder Read(ByteReader reader, bool withPayload, bool extended = true)
    {
        var order = new MarketOrder
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
        };
        if (extended)
        {
            order.NpcFaction = reader.ReadRequiredString();
            order.NpcFactionIndex = (int)reader.ReadVarInt();
        }
        order.Payload = withPayload ? reader.ReadBytes() : Array.Empty<byte>();
        return order;
    }

    public static void WriteList(ByteWriter writer, IReadOnlyList<MarketOrder> orders, bool withPayload)
    {
        writer.WriteVarUInt((ulong)orders.Count);
        foreach (var order in orders)
            order.Write(writer, withPayload);
    }

    public static List<MarketOrder> ReadList(ByteReader reader, bool withPayload, bool extended = true)
    {
        var count = reader.ReadVarUInt();
        if (count > 100_000)
            throw new ProtocolException($"Too many orders: {count}");
        var orders = new List<MarketOrder>((int)count);
        for (var i = 0UL; i < count; i++)
            orders.Add(Read(reader, withPayload, extended));
        return orders;
    }
}

/// <summary>
/// NPC factions on the world market. Each colony has its own relations with them, but the factions themselves (by def,
/// and which one of that def) are the same in every game of the world, so a deal names the faction that way and each
/// game finds it among its own. A faction's "owner key" on the market has the top bit set: never a SteamID.
/// </summary>
public static class NpcTrader
{
    private const ulong NpcBit = 0x8000_0000_0000_0000UL;

    public static ulong OwnerKey(string factionDef, int index)
    {
        // FNV-1a: the same key in every process and every run.
        var hash = 14695981039346656037UL;
        foreach (var ch in factionDef + "#" + index)
        {
            hash ^= ch;
            hash *= 1099511628211UL;
        }
        return NpcBit | (hash & ~NpcBit);
    }

    public static bool IsNpc(ulong owner) => (owner & NpcBit) != 0;
}
