using System;
using System.Linq;
using RimMult.Shared.Net;
using RimMult.Shared.Packets;
using RimMult.Shared.World;

namespace RimMult.ServerCore;

/// <summary>
/// The world's market: lots (goods for silver) and orders (silver for goods). The server holds both sides — the goods
/// of a lot, the reward of an order — so a deal goes through even while the other player is away. Whatever changes
/// hands leaves as parcels (<see cref="ParcelAddress.ForMarket"/>), delivered like any other.
/// <para>
/// NPC factions trade too: the server knows nothing about RimWorld's items, so one game in the world — the broker,
/// the host or else the player in the world longest — makes their lots and orders and takes their side of deals.
/// What NPCs buy and sell moves the world's prices (<see cref="MarketPrices"/>), and news moves whole categories.
/// </para>
/// </summary>
public sealed partial class GameServer
{
    private readonly Random _random = new();

    private MarketState MarketStatePacket() => new()
    {
        Lots = World.MarketLots,
        Orders = World.MarketOrders,
        BrokerPlayerId = BrokerId(),
        NpcMaxLots = Settings.Market.NpcMaxLots,
        NpcMaxOrders = Settings.Market.NpcMaxOrders,
        FreeListings = Settings.Market.FreeListings,
        FeePercent = Settings.Market.FeePercent,
        Prices = World.Prices,
    };

    private void BroadcastMarket() => Broadcast(PacketCodec.Encode(MarketStatePacket()), DeliveryMode.ReliableOrdered);

    /// <summary>The game that plays the NPC factions: the host if it is in the world, else whoever is in it longest.</summary>
    private int BrokerId()
    {
        if (!Settings.Market.NpcTraders || Settings.Mode == Shared.Coop.GameMode.Coop)
            return -1;
        var inWorld = Players.Where(p => p.InWorld).OrderBy(p => p.Id).ToList();
        if (inWorld.Count == 0)
            return -1;
        return (inWorld.FirstOrDefault(p => p.IsHost) ?? inWorld[0]).Id;
    }

    /// <summary>The fee the player owes for one more listing at <paramref name="price"/>.</summary>
    private int FeeFor(ulong owner, int price) => MarketState.FeeFor(price,
        World.MarketLots.Count(l => l.Seller == owner) + World.MarketOrders.Count(o => o.Buyer == owner),
        Settings.Market.FreeListings, Settings.Market.FeePercent);

    private void HandleMarket(PlayerInfo player, MarketAction action)
    {
        if (World.Definition == null || !player.InWorld)
            return;
        if (action.Kind >= MarketActionKind.NpcPostLot)
        {
            if (player.Id == BrokerId())
                HandleNpcMarket(action);
            return;
        }

        var me = OwnerKey(player);
        switch (action.Kind)
        {
            case MarketActionKind.PostLot:
                if (action.Payload.Length == 0 || action.Price <= 0 || action.Price > MarketLot.MaxPrice
                    || World.MarketLots.Count(l => l.Seller == me) >= MarketLot.MaxPerPlayer || action.Fee < FeeFor(me, action.Price))
                {
                    // Refused: the goods go straight back.
                    Mail(me, me, player.Name, ParcelAddress.ForMarket(ParcelAddress.MarketKind.LotReturned), Truncate(action.Summary, 1000), action.Payload);
                    return;
                }
                World.MarketLots.Add(new MarketLot
                {
                    Id = World.NextMarketId++,
                    Seller = me,
                    SellerName = player.Name,
                    Summary = Truncate(action.Summary, 1000),
                    Value = action.Value,
                    Price = action.Price,
                    PostedTick = World.Tick,
                    DefName = Truncate(action.DefName, 200),
                    Payload = action.Payload,
                });
                _log($"{player.Name} put up a lot for {action.Price} silver: {action.Summary}");
                break;

            case MarketActionKind.CancelLot:
                var mine = World.MarketLots.FirstOrDefault(l => l.Id == action.Id && l.Seller == me);
                if (mine == null)
                    return;
                World.MarketLots.Remove(mine);
                Mail(me, me, player.Name, ParcelAddress.ForMarket(ParcelAddress.MarketKind.LotReturned), mine.Summary, mine.Payload);
                break;

            case MarketActionKind.BuyLot:
                var lot = World.MarketLots.FirstOrDefault(l => l.Id == action.Id);
                if (lot == null || lot.Seller == me || lot.Price != action.Price || Diplomacy.Get(me, lot.Seller) == PlayerRelation.Hostile)
                {
                    // Sold meanwhile (or no deal with an enemy): the silver goes back.
                    Mail(me, me, player.Name, ParcelAddress.ForMarket(ParcelAddress.MarketKind.Refund), Truncate(action.Summary, 1000), action.Payload);
                    return;
                }
                World.MarketLots.Remove(lot);
                Mail(me, lot.Seller, lot.SellerName, ParcelAddress.ForMarket(ParcelAddress.MarketKind.Bought, lot.NpcFaction, lot.NpcFactionIndex), lot.Summary, lot.Payload);
                // An NPC's silver goes nowhere: its faction lives in each player's own game.
                Mail(lot.Seller, me, player.Name, ParcelAddress.ForMarket(ParcelAddress.MarketKind.Sold), Truncate(action.Summary, 1000), action.Payload);
                if (lot.IsNpc)
                    World.Prices.Bought(lot.DefName, lot.Price);
                _log($"{player.Name} bought {lot.SellerName}'s lot for {lot.Price} silver: {lot.Summary}");
                Record(ChronicleKind.MarketSale, me, lot.Seller, a: lot.Price, text: lot.Summary, targetName: lot.SellerName);
                break;

            case MarketActionKind.PostOrder:
                if (action.Payload.Length == 0 || action.Price <= 0 || action.Price > MarketLot.MaxPrice || action.Count <= 0
                    || action.Count > MarketOrder.MaxCount || action.DefName.Length == 0 || action.Days < TreatyTerms.MinDays
                    || World.MarketOrders.Count(o => o.Buyer == me) >= MarketOrder.MaxPerPlayer || action.Fee < FeeFor(me, action.Price))
                {
                    Mail(me, me, player.Name, ParcelAddress.ForMarket(ParcelAddress.MarketKind.Refund), Truncate(action.Summary, 1000), action.Payload);
                    return;
                }
                World.MarketOrders.Add(new MarketOrder
                {
                    Id = World.NextMarketId++,
                    Buyer = me,
                    BuyerName = player.Name,
                    DefName = Truncate(action.DefName, 200),
                    Label = Truncate(action.Label, 200),
                    Count = action.Count,
                    Reward = action.Price,
                    PostedTick = World.Tick,
                    DeadlineTick = World.Tick + (long)Math.Min(action.Days, TreatyTerms.MaxDays) * Treaty.TicksPerDay,
                    Payload = action.Payload,
                });
                _log($"{player.Name} ordered {action.Label} x{action.Count} for {action.Price} silver");
                break;

            case MarketActionKind.CancelOrder:
                var own = World.MarketOrders.FirstOrDefault(o => o.Id == action.Id && o.Buyer == me);
                if (own == null)
                    return;
                World.MarketOrders.Remove(own);
                Mail(me, me, player.Name, ParcelAddress.ForMarket(ParcelAddress.MarketKind.Refund), $"{own.Reward} silver", own.Payload);
                break;

            case MarketActionKind.FulfillOrder:
                var order = World.MarketOrders.FirstOrDefault(o => o.Id == action.Id);
                if (order == null || order.Buyer == me || Diplomacy.Get(me, order.Buyer) == PlayerRelation.Hostile)
                {
                    // Someone was faster (or the order is gone): the goods go back.
                    Mail(me, me, player.Name, ParcelAddress.ForMarket(ParcelAddress.MarketKind.LotReturned), Truncate(action.Summary, 1000), action.Payload);
                    return;
                }
                World.MarketOrders.Remove(order);
                var goods = Truncate(action.Summary, 1000);
                Mail(order.Buyer, me, player.Name, ParcelAddress.ForMarket(ParcelAddress.MarketKind.OrderDelivered), goods, action.Payload);
                Mail(me, order.Buyer, order.BuyerName, ParcelAddress.ForMarket(ParcelAddress.MarketKind.Reward, order.NpcFaction, order.NpcFactionIndex),
                    $"{order.Reward} silver", order.Payload);
                if (order.IsNpc)
                    World.Prices.Sold(order.DefName, order.Reward);
                _log($"{player.Name} delivered {order.BuyerName}'s order: {goods}");
                Record(ChronicleKind.OrderDelivered, me, order.Buyer, a: order.Reward, text: goods, targetName: order.BuyerName);
                break;
        }
        WorldChanged?.Invoke();
        BroadcastMarket();
    }

    /// <summary>The broker's game acting for an NPC faction.</summary>
    private void HandleNpcMarket(MarketAction action)
    {
        if (action.NpcFaction.Length == 0)
            return;
        var npc = NpcTrader.OwnerKey(action.NpcFaction, action.NpcFactionIndex);
        var name = Truncate(action.NpcName, 64);
        var days = Math.Max(1, Math.Min(60, action.Days));
        switch (action.Kind)
        {
            case MarketActionKind.NpcPostLot:
                if (action.Payload.Length == 0 || action.Price <= 0 || action.Price > MarketLot.MaxPrice
                    || World.MarketLots.Count(l => l.IsNpc) >= Settings.Market.NpcMaxLots)
                    return;
                World.MarketLots.Add(new MarketLot
                {
                    Id = World.NextMarketId++,
                    Seller = npc,
                    SellerName = name,
                    Summary = Truncate(action.Summary, 1000),
                    Value = action.Value,
                    Price = action.Price,
                    PostedTick = World.Tick,
                    ExpiresTick = World.Tick + days * (long)Treaty.TicksPerDay,
                    DefName = Truncate(action.DefName, 200),
                    NpcFaction = Truncate(action.NpcFaction, 200),
                    NpcFactionIndex = action.NpcFactionIndex,
                    Payload = action.Payload,
                });
                break;

            case MarketActionKind.NpcPostOrder:
                if (action.Payload.Length == 0 || action.Price <= 0 || action.Price > MarketLot.MaxPrice || action.Count <= 0
                    || action.Count > MarketOrder.MaxCount || action.DefName.Length == 0
                    || World.MarketOrders.Count(o => o.IsNpc) >= Settings.Market.NpcMaxOrders)
                    return;
                World.MarketOrders.Add(new MarketOrder
                {
                    Id = World.NextMarketId++,
                    Buyer = npc,
                    BuyerName = name,
                    DefName = Truncate(action.DefName, 200),
                    Label = Truncate(action.Label, 200),
                    Count = action.Count,
                    Reward = action.Price,
                    PostedTick = World.Tick,
                    DeadlineTick = World.Tick + days * (long)Treaty.TicksPerDay,
                    NpcFaction = Truncate(action.NpcFaction, 200),
                    NpcFactionIndex = action.NpcFactionIndex,
                    Payload = action.Payload,
                });
                break;

            case MarketActionKind.NpcBuyLot:
                var lot = World.MarketLots.FirstOrDefault(l => l.Id == action.Id);
                if (lot == null || lot.IsNpc || lot.Price != action.Price)
                    return;
                World.MarketLots.Remove(lot);
                Mail(lot.Seller, npc, name, ParcelAddress.ForMarket(ParcelAddress.MarketKind.Sold, action.NpcFaction, action.NpcFactionIndex),
                    Truncate(action.Summary, 1000), action.Payload);
                World.Prices.Sold(lot.DefName, lot.Price);
                _log($"{name} bought {lot.SellerName}'s lot for {lot.Price} silver: {lot.Summary}");
                Record(ChronicleKind.MarketSale, npc, lot.Seller, a: lot.Price, text: lot.Summary, actorName: name);
                break;

            case MarketActionKind.NpcFulfillOrder:
                var order = World.MarketOrders.FirstOrDefault(o => o.Id == action.Id);
                if (order == null || order.IsNpc)
                    return;
                World.MarketOrders.Remove(order);
                var goods = Truncate(action.Summary, 1000);
                Mail(order.Buyer, npc, name, ParcelAddress.ForMarket(ParcelAddress.MarketKind.OrderDelivered, action.NpcFaction, action.NpcFactionIndex),
                    goods, action.Payload);
                World.Prices.Bought(order.DefName, order.Reward);
                _log($"{name} delivered {order.BuyerName}'s order: {goods}");
                Record(ChronicleKind.OrderDelivered, npc, order.Buyer, a: order.Reward, text: goods, actorName: name);
                break;
        }
        WorldChanged?.Invoke();
        BroadcastMarket();
    }

    /// <summary>
    /// With the clock: players' orders past their deadline refund the buyer, NPC lots and orders run out, prices drift
    /// back to normal and now and then there is news.
    /// </summary>
    private void CheckMarket()
    {
        if (World.Definition == null)
            return;
        var now = World.Tick;
        var changed = false;
        foreach (var order in World.MarketOrders.Where(o => o.DeadlineTick <= now).ToList())
        {
            World.MarketOrders.Remove(order);
            Mail(order.Buyer, order.Buyer, order.BuyerName, ParcelAddress.ForMarket(ParcelAddress.MarketKind.OrderExpired),
                $"{order.Label} x{order.Count}", order.Payload);
            changed = true;
        }
        changed |= World.MarketLots.RemoveAll(l => l.ExpiresTick > 0 && l.ExpiresTick <= now) > 0;
        changed |= World.Prices.Drift(now);
        if (Settings.Market.NpcTraders && Settings.Mode != Shared.Coop.GameMode.Coop && World.Prices.MaybeNews(now, _random) is { } news)
        {
            changed = true;
            Record(ChronicleKind.MarketNews, 0, a: (int)Math.Round((news.Factor - 1f) * 100f), text: news.Category, actorName: "");
        }
        if (!changed)
            return;
        WorldChanged?.Invoke();
        BroadcastMarket();
    }

    /// <summary>A parcel made by the server (market deals): kept until its recipient confirms it. Nothing goes to an NPC.</summary>
    private void Mail(ulong to, ulong from, string fromName, string address, string summary, byte[] payload)
    {
        if (NpcTrader.IsNpc(to))
            return;
        var item = new MailItem
        {
            Id = World.NextMailId++,
            FromOwner = from,
            FromName = fromName,
            ToOwner = to,
            ToTile = address,
            Summary = summary,
            Payload = payload,
        };
        World.Mail.Add(item);
        WorldChanged?.Invoke();
        foreach (var session in _sessions.Values)
        {
            if (session.Player is { InWorld: true } target && OwnerKey(target) == to)
                Send(session, new ParcelDeliver { Item = item });
        }
    }
}
