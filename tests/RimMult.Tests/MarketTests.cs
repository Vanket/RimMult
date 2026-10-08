using RimMult.ClientCore;
using RimMult.ServerCore;
using RimMult.Shared.Mods;
using RimMult.Shared.Packets;
using RimMult.Shared.Trade;
using RimMult.Shared.World;

namespace RimMult.Tests;

public class MarketTests
{
    private sealed class Host
    {
        public readonly TransportHub Hub = new();
        public readonly GameServer Server;
        public readonly LoopbackEndpoint Loopback;
        private double _time;

        public Host(WorldState world)
        {
            Server = new GameServer(new ServerSettings { AllowPvp = true }, Hub, world: world);
            Hub.Attach(Server);
            Loopback = new LoopbackEndpoint(Hub);
            Hub.AddEndpoint(Loopback);
        }

        public ClientSession Join(string name, ulong steamId)
        {
            var session = new ClientSession(Loopback.CreateClient(), new ClientHello
            {
                SteamId = steamId,
                DisplayName = name,
                GameVersion = "1.6",
                Mods = [new ModEntry("ludeon.rimworld", "Core", "1.6", 0)],
            });
            session.Start();
            return session;
        }

        public void Pump(params ClientSession[] sessions)
        {
            for (var i = 0; i < 20; i++)
            {
                Hub.Poll();
                Server.Update(_time += 1);
                foreach (var session in sessions)
                    session.Poll();
            }
        }
    }

    private static WorldState World() => new()
    {
        Definition = new WorldDefinition { WorldId = "w", SeedString = "s" },
        Tick = 1000,
        Colonies =
        [
            new ColonyInfo { OwnerSteamId = 1, OwnerName = "A", Name = "Home A", Tile = "10" },
            new ColonyInfo { OwnerSteamId = 2, OwnerName = "B", Name = "Home B", Tile = "20" },
        ],
    };

    private static (Host Host, WorldState World, ClientSession A, ClientSession B, List<MailItem> InboxA, List<MailItem> InboxB) Two()
    {
        var world = World();
        var host = new Host(world);
        var a = host.Join("A", 1);
        var b = host.Join("B", 2);
        host.Pump(a, b);
        a.EnterWorld("w");
        b.EnterWorld("w");
        host.Pump(a, b);
        var inboxA = new List<MailItem>();
        var inboxB = new List<MailItem>();
        a.ParcelReceived += inboxA.Add;
        b.ParcelReceived += inboxB.Add;
        return (host, world, a, b, inboxA, inboxB);
    }

    private static ParcelAddress.MarketKind KindOf(MailItem item)
    {
        Assert.True(ParcelAddress.TryParseMarket(item.ToTile, out var kind));
        return kind;
    }

    [Fact]
    public void LotSellsWhileTheSellerIsAway()
    {
        var (host, world, a, b, inboxA, inboxB) = Two();
        a.SendMarket(new MarketAction { Kind = MarketActionKind.PostLot, Price = 150, Value = 180, Summary = "Steel x200", Payload = [7, 7] });
        host.Pump(a, b);
        var lot = Assert.Single(b.MarketLots);
        Assert.Equal(("A", 150, "Steel x200"), (lot.SellerName, lot.Price, lot.Summary));
        Assert.Empty(lot.Payload); // the goods stay with the server

        a.LeaveWorld();
        host.Pump(a, b);
        b.SendMarket(new MarketAction { Kind = MarketActionKind.BuyLot, Id = lot.Id, Price = 150, Summary = "Silver x150", Payload = [1] });
        host.Pump(a, b);

        var bought = Assert.Single(inboxB);
        Assert.Equal(ParcelAddress.MarketKind.Bought, KindOf(bought));
        Assert.Equal(new byte[] { 7, 7 }, bought.Payload);
        Assert.Empty(b.MarketLots);
        Assert.Contains(world.Chronicle, e => e.Kind == ChronicleKind.MarketSale && e.A == 150);

        // The seller's silver waits for them.
        Assert.Empty(inboxA);
        a.EnterWorld("w");
        host.Pump(a, b);
        var sold = Assert.Single(inboxA);
        Assert.Equal(ParcelAddress.MarketKind.Sold, KindOf(sold));
        Assert.Equal(new byte[] { 1 }, sold.Payload);
    }

    [Fact]
    public void TooLateOrWrongPriceGetsTheSilverBack()
    {
        var (host, _, a, b, _, inboxB) = Two();
        a.SendMarket(new MarketAction { Kind = MarketActionKind.PostLot, Price = 100, Summary = "Gold x5", Payload = [5] });
        host.Pump(a, b);
        var id = b.MarketLots.Single().Id;

        b.SendMarket(new MarketAction { Kind = MarketActionKind.BuyLot, Id = id, Price = 90, Payload = [9] });
        host.Pump(a, b);
        Assert.Equal(ParcelAddress.MarketKind.Refund, KindOf(Assert.Single(inboxB)));
        Assert.Single(b.MarketLots);

        a.SendMarket(new MarketAction { Kind = MarketActionKind.CancelLot, Id = id });
        host.Pump(a, b);
        b.SendMarket(new MarketAction { Kind = MarketActionKind.BuyLot, Id = id, Price = 100, Payload = [9] });
        host.Pump(a, b);
        Assert.Equal(2, inboxB.Count(i => KindOf(i) == ParcelAddress.MarketKind.Refund));
        Assert.Empty(b.MarketLots);
    }

    [Fact]
    public void OnlyTheSellerTakesALotBack()
    {
        var (host, _, a, b, inboxA, inboxB) = Two();
        a.SendMarket(new MarketAction { Kind = MarketActionKind.PostLot, Price = 100, Summary = "Gold x5", Payload = [5] });
        host.Pump(a, b);
        var id = a.MarketLots.Single().Id;

        b.SendMarket(new MarketAction { Kind = MarketActionKind.CancelLot, Id = id });
        host.Pump(a, b);
        Assert.Single(a.MarketLots);

        a.SendMarket(new MarketAction { Kind = MarketActionKind.CancelLot, Id = id });
        host.Pump(a, b);
        Assert.Equal(ParcelAddress.MarketKind.LotReturned, KindOf(Assert.Single(inboxA)));
        Assert.Empty(inboxB);
    }

    [Fact]
    public void OrderPaysWhoeverDelivers()
    {
        var (host, world, a, b, inboxA, inboxB) = Two();
        a.SendMarket(new MarketAction
        {
            Kind = MarketActionKind.PostOrder, DefName = "MedicineIndustrial", Label = "medicine", Count = 10, Price = 400, Days = 15,
            Summary = "Silver x400", Payload = [4],
        });
        host.Pump(a, b);
        var order = Assert.Single(b.MarketOrders);
        Assert.Equal(("medicine", 10, 400), (order.Label, order.Count, order.Reward));
        Assert.Equal(1000 + 15L * Treaty.TicksPerDay, order.DeadlineTick);

        // Not one's own.
        a.SendMarket(new MarketAction { Kind = MarketActionKind.FulfillOrder, Id = order.Id, Summary = "medicine x10", Payload = [8] });
        host.Pump(a, b);
        Assert.Single(b.MarketOrders);

        b.SendMarket(new MarketAction { Kind = MarketActionKind.FulfillOrder, Id = order.Id, Summary = "medicine x10", Payload = [8] });
        host.Pump(a, b);
        Assert.Empty(a.MarketOrders);
        Assert.Contains(inboxA, i => KindOf(i) == ParcelAddress.MarketKind.OrderDelivered && i.Payload.SequenceEqual(new byte[] { 8 }));
        Assert.Equal(ParcelAddress.MarketKind.Reward, KindOf(Assert.Single(inboxB)));
        Assert.Equal(new byte[] { 4 }, inboxB[0].Payload);
        Assert.Contains(world.Chronicle, e => e.Kind == ChronicleKind.OrderDelivered && e.A == 400);
    }

    [Fact]
    public void ExpiredOrderRefundsTheBuyer()
    {
        var (host, world, a, b, inboxA, _) = Two();
        a.SendMarket(new MarketAction { Kind = MarketActionKind.PostOrder, DefName = "Steel", Label = "steel", Count = 1, Price = 10, Days = 2, Payload = [3] });
        host.Pump(a, b);
        Assert.Single(world.MarketOrders);

        world.Tick += 3L * Treaty.TicksPerDay;
        host.Pump(a, b);
        Assert.Empty(world.MarketOrders);
        Assert.Empty(b.MarketOrders);
        Assert.Equal(ParcelAddress.MarketKind.OrderExpired, KindOf(Assert.Single(inboxA)));
    }

    [Fact]
    public void NoDealsWithAnEnemyAndTheMarketSurvivesARestart()
    {
        var (host, world, a, b, _, inboxB) = Two();
        a.SendMarket(new MarketAction { Kind = MarketActionKind.PostLot, Price = 100, Summary = "Gold x5", Payload = [5] });
        a.SendMarket(new MarketAction { Kind = MarketActionKind.PostOrder, DefName = "Steel", Label = "steel", Count = 5, Price = 50, Days = 10, Payload = [6] });
        a.SendDiplomacy(2, DiplomacyAction.DeclareWar);
        host.Pump(a, b);

        b.SendMarket(new MarketAction { Kind = MarketActionKind.BuyLot, Id = b.MarketLots.Single().Id, Price = 100, Payload = [9] });
        host.Pump(a, b);
        Assert.Equal(ParcelAddress.MarketKind.Refund, KindOf(Assert.Single(inboxB)));

        var copy = WorldState.Deserialize(world.Serialize());
        Assert.Equal(new byte[] { 5 }, Assert.Single(copy.MarketLots).Payload);
        Assert.Equal(new byte[] { 6 }, Assert.Single(copy.MarketOrders).Payload);
        Assert.True(copy.NextMarketId > 2);
    }

    [Fact]
    public void ShowcaseAndWishesTravel()
    {
        var toA = new Queue<TradeMessage>();
        var toB = new Queue<TradeMessage>();
        // Each side's messages are encoded and decoded, as on the wire.
        var a = new PlayerTrade(2, "B", TradeState.Open, m => toB.Enqueue(TradeMessage.Decode(m.Encode())));
        var b = new PlayerTrade(1, "A", TradeState.Open, m => toA.Enqueue(TradeMessage.Decode(m.Encode())));
        void Flush()
        {
            while (toA.Count > 0 || toB.Count > 0)
            {
                while (toB.Count > 0)
                    b.Receive(toB.Dequeue());
                while (toA.Count > 0)
                    a.Receive(toA.Dequeue());
            }
        }

        a.SetCatalog([new TradeLine { Key = "0", Label = "Steel", Count = 300, Value = 1.9f }, new TradeLine { Key = "1", Label = "Jade", Count = 20, Value = 5f }]);
        Flush();
        Assert.Equal(["Steel", "Jade"], b.TheirCatalog.Select(l => l.Label));

        b.Request("0", 500); // more than there is: capped
        b.Request("1", 4);
        Flush();
        Assert.Equal(300, a.TheirRequests["0"]);
        Assert.Equal(4, a.TheirRequests["1"]);

        b.Request("1", 0);
        Flush();
        Assert.False(a.TheirRequests.ContainsKey("1"));

        // The showcase shrinks: so do the wishes.
        a.SetCatalog([new TradeLine { Key = "0", Label = "Steel", Count = 100, Value = 1.9f }]);
        Flush();
        Assert.Equal(100, b.MyRequests["0"]);
    }

    [Fact]
    public void ListingsBeyondTheFreeOnesCostAFee()
    {
        var (host, world, a, b, inboxA, _) = Two();
        for (var i = 0; i < 3; i++)
            a.SendMarket(new MarketAction { Kind = MarketActionKind.PostLot, Price = 100, Summary = "x", Payload = [1] });
        host.Pump(a, b);
        Assert.Equal(3, world.MarketLots.Count);
        Assert.Equal(10, a.MarketFeeFor(200)); // 5 % from the fourth on

        // Unpaid: the goods come back.
        a.SendMarket(new MarketAction { Kind = MarketActionKind.PostLot, Price = 200, Summary = "x", Payload = [1] });
        host.Pump(a, b);
        Assert.Equal(3, world.MarketLots.Count);
        Assert.Equal(ParcelAddress.MarketKind.LotReturned, KindOf(Assert.Single(inboxA)));

        a.SendMarket(new MarketAction { Kind = MarketActionKind.PostLot, Price = 200, Fee = 10, Summary = "x", Payload = [1] });
        host.Pump(a, b);
        Assert.Equal(4, world.MarketLots.Count);
        Assert.Equal(0, b.MarketFeeFor(200)); // the other player still has all theirs
    }

    [Fact]
    public void TheBrokerIsTheHostOrTheLongestInTheWorld()
    {
        var (host, _, a, b, _, _) = Two();
        Assert.Equal(a.PlayerId, b.Market.BrokerPlayerId);
        Assert.True(a.IsMarketBroker);
        Assert.False(b.IsMarketBroker);

        // Only the broker plays the NPCs.
        b.SendMarket(new MarketAction { Kind = MarketActionKind.NpcPostLot, NpcFaction = "OutlanderCivil", NpcName = "Union", Price = 50, Days = 5, Payload = [1] });
        host.Pump(a, b);
        Assert.Empty(a.MarketLots);

        a.LeaveWorld();
        host.Pump(a, b);
        Assert.True(b.IsMarketBroker);
        b.SendMarket(new MarketAction { Kind = MarketActionKind.NpcPostLot, NpcFaction = "OutlanderCivil", NpcName = "Union", Price = 50, Days = 5, Payload = [1] });
        host.Pump(a, b);
        Assert.Single(b.MarketLots);
    }

    [Fact]
    public void NpcLotSellsToAPlayerRunsOutAndMovesThePrice()
    {
        var (host, world, a, b, _, inboxB) = Two();
        a.SendMarket(new MarketAction
        {
            Kind = MarketActionKind.NpcPostLot, NpcFaction = "OutlanderCivil", NpcFactionIndex = 1, NpcName = "Union",
            DefName = "ComponentIndustrial", Summary = "component x20", Value = 640, Price = 800, Days = 10, Payload = [3],
        });
        a.SendMarket(new MarketAction { Kind = MarketActionKind.NpcPostLot, NpcFaction = "Pirate", NpcName = "Raiders", Price = 90, Days = 2, Payload = [4] });
        host.Pump(a, b);
        var lot = b.MarketLots.Single(l => l.SellerName == "Union");
        Assert.True(lot.IsNpc && NpcTrader.IsNpc(lot.Seller));
        Assert.Equal(1000 + 10L * Treaty.TicksPerDay, lot.ExpiresTick);

        b.SendMarket(new MarketAction { Kind = MarketActionKind.BuyLot, Id = lot.Id, Price = 800, Payload = [8] });
        host.Pump(a, b);
        var bought = Assert.Single(inboxB);
        Assert.True(ParcelAddress.TryParseMarket(bought.ToTile, out var kind, out var faction, out var index));
        Assert.Equal((ParcelAddress.MarketKind.Bought, "OutlanderCivil", 1), (kind, faction, index));
        Assert.True(world.Prices.ItemFactor("ComponentIndustrial") > 1f);
        // The NPC's silver goes nowhere.
        Assert.DoesNotContain(world.Mail, m => NpcTrader.IsNpc(m.ToOwner));

        // The other one runs out unsold.
        world.Tick += 3L * Treaty.TicksPerDay;
        host.Pump(a, b);
        Assert.Empty(b.MarketLots);
    }

    [Fact]
    public void NpcsBuyPlayersLotsAndDeliverTheirOrders()
    {
        var (host, world, a, b, inboxA, inboxB) = Two();
        b.SendMarket(new MarketAction { Kind = MarketActionKind.PostLot, DefName = "Steel", Price = 100, Value = 190, Summary = "steel x100", Payload = [1] });
        b.SendMarket(new MarketAction { Kind = MarketActionKind.PostOrder, DefName = "MedicineIndustrial", Label = "medicine", Count = 5, Price = 300, Days = 10, Payload = [2] });
        host.Pump(a, b);

        a.SendMarket(new MarketAction
        {
            Kind = MarketActionKind.NpcBuyLot, Id = b.MarketLots.Single().Id, Price = 100, NpcFaction = "OutlanderCivil", NpcName = "Union", Payload = [9],
        });
        a.SendMarket(new MarketAction
        {
            Kind = MarketActionKind.NpcFulfillOrder, Id = b.MarketOrders.Single().Id, NpcFaction = "Empire", NpcName = "Empire", Summary = "medicine x5", Payload = [7],
        });
        host.Pump(a, b);

        Assert.Empty(b.MarketLots);
        Assert.Empty(b.MarketOrders);
        Assert.Contains(inboxB, m => KindOf(m) == ParcelAddress.MarketKind.Sold && m.Payload.SequenceEqual(new byte[] { 9 }) && m.FromName == "Union");
        Assert.Contains(inboxB, m => KindOf(m) == ParcelAddress.MarketKind.OrderDelivered && m.Payload.SequenceEqual(new byte[] { 7 }));
        Assert.True(world.Prices.ItemFactor("Steel") < 1f);
        Assert.True(world.Prices.ItemFactor("MedicineIndustrial") > 1f);
        Assert.Contains(world.Chronicle, e => e.Kind == ChronicleKind.MarketSale && e.ActorName == "Union");
        Assert.Empty(inboxA);
    }

    [Fact]
    public void PlayerDeliversAnNpcOrder()
    {
        var (host, world, a, b, _, inboxB) = Two();
        a.SendMarket(new MarketAction
        {
            Kind = MarketActionKind.NpcPostOrder, NpcFaction = "OutlanderCivil", NpcName = "Union", DefName = "Steel", Label = "steel",
            Count = 100, Price = 260, Days = 15, Payload = [5],
        });
        host.Pump(a, b);
        var order = b.MarketOrders.Single();
        Assert.True(order.IsNpc);

        b.SendMarket(new MarketAction { Kind = MarketActionKind.FulfillOrder, Id = order.Id, Summary = "steel x100", Payload = [6] });
        host.Pump(a, b);
        var reward = Assert.Single(inboxB);
        Assert.True(ParcelAddress.TryParseMarket(reward.ToTile, out var kind, out var faction, out _));
        Assert.Equal((ParcelAddress.MarketKind.Reward, "OutlanderCivil"), (kind, faction));
        Assert.Equal(new byte[] { 5 }, reward.Payload);
        Assert.True(world.Prices.ItemFactor("Steel") < 1f);
        Assert.DoesNotContain(world.Mail, m => NpcTrader.IsNpc(m.ToOwner));
    }

    [Fact]
    public void PricesDriftBackAndNewsComes()
    {
        var prices = new MarketPrices();
        prices.Bought("Gold", 3000);
        Assert.Equal(1f + MarketPrices.MaxStepPerDeal, prices.ItemFactor("Gold"), 3);
        for (var i = 0; i < 30; i++)
            prices.Bought("Gold", 100_000);
        Assert.Equal(MarketPrices.MaxFactor, prices.ItemFactor("Gold"));

        prices.Drift(1000);
        Assert.False(prices.Drift(1000 + 30_000)); // less than a day
        Assert.True(prices.Drift(1000 + 10L * 60_000));
        Assert.True(prices.ItemFactor("Gold") < MarketPrices.MaxFactor);
        prices.Drift(1000 + 200L * 60_000);
        Assert.Equal(1f, prices.ItemFactor("Gold")); // back to normal and forgotten
        Assert.Empty(prices.Items);

        var random = new Random(7);
        Assert.Null(prices.MaybeNews(0, random)); // plans the first one
        var news = prices.MaybeNews(prices.NextNewsTick, random);
        Assert.NotNull(news);
        Assert.Contains(news!.Category, MarketPrices.NewsCategories);
        Assert.NotEqual(1f, prices.NewsFactor([news.Category], news.StartTick));
        Assert.Equal(1f, prices.NewsFactor(["Something"], news.StartTick));
        Assert.Equal(1f, prices.NewsFactor([news.Category], news.EndTick));

        var copy = Assert.IsType<MarketState>(PacketCodec.Decode(PacketCodec.Encode(new MarketState { Prices = prices, BrokerPlayerId = 3, FeePercent = 5 })));
        Assert.Equal((3, 5, news.Category), (copy.BrokerPlayerId, copy.FeePercent, copy.Prices.News.Single().Category));
    }

    [Fact]
    public void Version8WorldFilesLoad()
    {
        var bytes = World().Serialize();
        // Format 8 is the current one without the world's prices at the end (two empty lists and two zero ticks) and the planet (two bytes).
        var old = bytes.Take(bytes.Length - 4 - 2).ToArray();
        old[0] = 8;
        var state = WorldState.Deserialize(old);
        Assert.Equal(2, state.Colonies.Count);
        Assert.Empty(state.Prices.Items);
    }
}
