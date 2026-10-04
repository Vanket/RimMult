using System.Linq;
using RimMult.Shared.Net;
using RimMult.Shared.Packets;
using RimMult.Shared.World;

namespace RimMult.ServerCore;

/// <summary>
/// The world's market: lots (goods for silver) and orders (silver for goods). The server holds both sides — the goods
/// of a lot, the reward of an order — so a deal goes through even while the other player is away. Whatever changes
/// hands leaves as parcels (<see cref="ParcelAddress.ForMarket"/>), delivered like any other.
/// </summary>
public sealed partial class GameServer
{
    private MarketState MarketStatePacket() => new() { Lots = World.MarketLots, Orders = World.MarketOrders };

    private void BroadcastMarket() => Broadcast(PacketCodec.Encode(MarketStatePacket()), DeliveryMode.ReliableOrdered);

    private void HandleMarket(PlayerInfo player, MarketAction action)
    {
        if (World.Definition == null || !player.InWorld)
            return;
        var me = OwnerKey(player);
        switch (action.Kind)
        {
            case MarketActionKind.PostLot:
                if (action.Payload.Length == 0 || action.Price <= 0 || action.Price > MarketLot.MaxPrice
                    || World.MarketLots.Count(l => l.Seller == me) >= MarketLot.MaxPerPlayer)
                {
                    // Refused: the goods go straight back.
                    Mail(me, me, player.Name, ParcelAddress.MarketKind.LotReturned, Truncate(action.Summary, 1000), action.Payload);
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
                    Payload = action.Payload,
                });
                _log($"{player.Name} put up a lot for {action.Price} silver: {action.Summary}");
                break;

            case MarketActionKind.CancelLot:
                var mine = World.MarketLots.FirstOrDefault(l => l.Id == action.Id && l.Seller == me);
                if (mine == null)
                    return;
                World.MarketLots.Remove(mine);
                Mail(me, me, player.Name, ParcelAddress.MarketKind.LotReturned, mine.Summary, mine.Payload);
                break;

            case MarketActionKind.BuyLot:
                var lot = World.MarketLots.FirstOrDefault(l => l.Id == action.Id);
                if (lot == null || lot.Seller == me || lot.Price != action.Price || Diplomacy.Get(me, lot.Seller) == PlayerRelation.Hostile)
                {
                    // Sold meanwhile (or no deal with an enemy): the silver goes back.
                    Mail(me, me, player.Name, ParcelAddress.MarketKind.Refund, Truncate(action.Summary, 1000), action.Payload);
                    return;
                }
                World.MarketLots.Remove(lot);
                Mail(me, lot.Seller, lot.SellerName, ParcelAddress.MarketKind.Bought, lot.Summary, lot.Payload);
                Mail(lot.Seller, me, player.Name, ParcelAddress.MarketKind.Sold, Truncate(action.Summary, 1000), action.Payload);
                _log($"{player.Name} bought {lot.SellerName}'s lot for {lot.Price} silver: {lot.Summary}");
                Record(ChronicleKind.MarketSale, me, lot.Seller, a: lot.Price, text: lot.Summary);
                break;

            case MarketActionKind.PostOrder:
                if (action.Payload.Length == 0 || action.Price <= 0 || action.Price > MarketLot.MaxPrice || action.Count <= 0
                    || action.Count > MarketOrder.MaxCount || action.DefName.Length == 0 || action.Days < TreatyTerms.MinDays
                    || World.MarketOrders.Count(o => o.Buyer == me) >= MarketOrder.MaxPerPlayer)
                {
                    Mail(me, me, player.Name, ParcelAddress.MarketKind.Refund, Truncate(action.Summary, 1000), action.Payload);
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
                    DeadlineTick = World.Tick + (long)System.Math.Min(action.Days, TreatyTerms.MaxDays) * Treaty.TicksPerDay,
                    Payload = action.Payload,
                });
                _log($"{player.Name} ordered {action.Label} x{action.Count} for {action.Price} silver");
                break;

            case MarketActionKind.CancelOrder:
                var own = World.MarketOrders.FirstOrDefault(o => o.Id == action.Id && o.Buyer == me);
                if (own == null)
                    return;
                World.MarketOrders.Remove(own);
                Mail(me, me, player.Name, ParcelAddress.MarketKind.Refund, $"{own.Reward} silver", own.Payload);
                break;

            case MarketActionKind.FulfillOrder:
                var order = World.MarketOrders.FirstOrDefault(o => o.Id == action.Id);
                if (order == null || order.Buyer == me || Diplomacy.Get(me, order.Buyer) == PlayerRelation.Hostile)
                {
                    // Someone was faster (or the order is gone): the goods go back.
                    Mail(me, me, player.Name, ParcelAddress.MarketKind.LotReturned, Truncate(action.Summary, 1000), action.Payload);
                    return;
                }
                World.MarketOrders.Remove(order);
                var goods = Truncate(action.Summary, 1000);
                Mail(order.Buyer, me, player.Name, ParcelAddress.MarketKind.OrderDelivered, goods, action.Payload);
                Mail(me, order.Buyer, order.BuyerName, ParcelAddress.MarketKind.Reward, $"{order.Reward} silver", order.Payload);
                _log($"{player.Name} delivered {order.BuyerName}'s order: {goods}");
                Record(ChronicleKind.OrderDelivered, me, order.Buyer, a: order.Reward, text: goods);
                break;
        }
        WorldChanged?.Invoke();
        BroadcastMarket();
    }

    /// <summary>Orders past their deadline: the reward goes back to the buyer.</summary>
    private void CheckMarket()
    {
        var expired = World.MarketOrders.Where(o => o.DeadlineTick <= World.Tick).ToList();
        if (expired.Count == 0)
            return;
        foreach (var order in expired)
        {
            World.MarketOrders.Remove(order);
            Mail(order.Buyer, order.Buyer, order.BuyerName, ParcelAddress.MarketKind.OrderExpired, $"{order.Label} x{order.Count}", order.Payload);
        }
        WorldChanged?.Invoke();
        BroadcastMarket();
    }

    /// <summary>A parcel made by the server (market deals): kept until its recipient confirms it, like any other.</summary>
    private void Mail(ulong to, ulong from, string fromName, ParcelAddress.MarketKind kind, string summary, byte[] payload)
    {
        var item = new MailItem
        {
            Id = World.NextMailId++,
            FromOwner = from,
            FromName = fromName,
            ToOwner = to,
            ToTile = ParcelAddress.ForMarket(kind),
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
