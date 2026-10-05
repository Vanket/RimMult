using System.Linq;
using RimMult.Shared.Net;
using RimMult.Shared.Packets;
using RimMult.Shared.World;

namespace RimMult.ServerCore;

/// <summary>
/// The admin panel (the host and admins only): gifts of items and research to any player (online or not, they come
/// like parcels), the market, relations between players, and a pause of the world for everyone. Nothing of it goes
/// into the chronicle.
/// </summary>
public sealed partial class GameServer
{
    /// <summary>The "player" whose hold keeps the world paused for an admin's pause.</summary>
    private const int AdminPauseHold = int.MinValue;

    /// <summary>Somebody this world knows: playing now, or with a colony.</summary>
    private bool KnownOwner(ulong owner) =>
        owner != 0 && (Players.Any(p => OwnerKey(p) == owner) || World.Colonies.Any(c => c.OwnerSteamId == owner));

    private void HandleAdmin(PlayerInfo player, AdminAction action)
    {
        if (!IsAdmin(player))
        {
            _log($"{player.Name} tried an admin action ({action.Kind}) without being an admin");
            return;
        }
        var me = OwnerKey(player);
        switch (action.Kind)
        {
            case AdminActionKind.GiveItems when KnownOwner(action.Target) && action.Payload.Length > 0:
                Mail(action.Target, me, player.Name, ParcelAddress.ForAdminGift(), Truncate(action.Summary, 1000), action.Payload);
                break;
            case AdminActionKind.GiveResearch when KnownOwner(action.Target) && action.Payload.Length > 0:
                Mail(action.Target, me, player.Name, ParcelAddress.ForAdminResearch(), Truncate(action.Summary, 1000), action.Payload);
                break;
            case AdminActionKind.RemoveLot:
                var lot = World.MarketLots.FirstOrDefault(l => l.Id == action.Id);
                if (lot == null)
                    return;
                World.MarketLots.Remove(lot);
                Mail(lot.Seller, lot.Seller, lot.SellerName, ParcelAddress.ForMarket(ParcelAddress.MarketKind.LotReturned), lot.Summary, lot.Payload);
                BroadcastMarket();
                break;
            case AdminActionKind.RemoveOrder:
                var order = World.MarketOrders.FirstOrDefault(o => o.Id == action.Id);
                if (order == null)
                    return;
                World.MarketOrders.Remove(order);
                Mail(order.Buyer, order.Buyer, order.BuyerName, ParcelAddress.ForMarket(ParcelAddress.MarketKind.Refund), $"{order.Reward} silver", order.Payload);
                BroadcastMarket();
                break;
            case AdminActionKind.ResetPrices:
                World.Prices.Items.Clear();
                World.Prices.News.Clear();
                BroadcastMarket();
                break;
            case AdminActionKind.MarketNews:
                World.Prices.NextNewsTick = System.Math.Max(1, World.Tick);
                if (World.Prices.MaybeNews(World.Tick, _random) is { } news)
                    Record(ChronicleKind.MarketNews, 0, a: (int)System.Math.Round((news.Factor - 1f) * 100f), text: news.Category, actorName: "");
                BroadcastMarket();
                break;
            case AdminActionKind.SetRelation when KnownOwner(action.Target) && KnownOwner(action.Other):
                Diplomacy.Force(action.Target, action.Other, action.Relation);
                Broadcast(PacketCodec.Encode(WorldUpdatePacket()), DeliveryMode.ReliableOrdered);
                break;
            case AdminActionKind.BreakTreaties:
                if (Diplomacy.EndTreaties(action.Target, action.Other))
                    Broadcast(PacketCodec.Encode(WorldUpdatePacket()), DeliveryMode.ReliableOrdered);
                break;
            case AdminActionKind.PauseWorld:
                Time.SetHold(AdminPauseHold, true);
                break;
            case AdminActionKind.ResumeWorld:
                Time.SetHold(AdminPauseHold, false);
                break;
            default:
                return;
        }
        _log($"Admin {player.Name}: {action.Kind}{(action.Summary.Length > 0 ? " " + action.Summary : "")}");
        WorldChanged?.Invoke();
    }
}
