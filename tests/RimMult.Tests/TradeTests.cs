using RimMult.ClientCore;
using RimMult.ServerCore;
using RimMult.Shared.Mods;
using RimMult.Shared.Packets;
using RimMult.Shared.Trade;
using RimMult.Shared.World;

namespace RimMult.Tests;

public class TradeTests
{
    /// <summary>Two trades wired back to back with an ordered "network" that only moves when told to.</summary>
    private sealed class Pair
    {
        public readonly Queue<TradeMessage> ToA = new();
        public readonly Queue<TradeMessage> ToB = new();
        public readonly PlayerTrade A;
        public readonly PlayerTrade B;
        public int CompletedA;
        public int CompletedB;

        public Pair()
        {
            A = new PlayerTrade(2, "B", TradeState.Open, m => ToB.Enqueue(m));
            B = new PlayerTrade(1, "A", TradeState.Open, m => ToA.Enqueue(m));
            A.Completed += () => CompletedA++;
            B.Completed += () => CompletedB++;
        }

        /// <summary>Delivers everything in flight (both directions) until quiet.</summary>
        public void Flush()
        {
            while (ToA.Count > 0 || ToB.Count > 0)
            {
                while (ToB.Count > 0)
                    B.Receive(ToB.Dequeue());
                while (ToA.Count > 0)
                    A.Receive(ToA.Dequeue());
            }
        }
    }

    private static List<TradeLine> Offer(string label, int count, float value = 10) =>
        [new TradeLine { Label = label, Count = count, Value = value }];

    [Fact]
    public void BothAcceptingCompletesOnBothSidesExactlyOnce()
    {
        var p = new Pair();
        p.A.SetMyOffer(Offer("Steel", 100));
        p.B.SetMyOffer(Offer("Gold", 5));
        p.Flush();
        Assert.Equal("Gold", p.A.TheirOffer.Single().Label);

        p.A.SetAccepted(true);
        p.Flush();
        Assert.True(p.B.TheyAccepted);
        Assert.Equal(TradeState.Open, p.B.State);

        p.B.SetAccepted(true);
        p.Flush();

        Assert.Equal(TradeState.Completed, p.A.State);
        Assert.Equal(TradeState.Completed, p.B.State);
        Assert.Equal(1, p.CompletedA);
        Assert.Equal(1, p.CompletedB);
    }

    [Fact]
    public void ChangingAnOfferVoidsTheOtherSidesAcceptance()
    {
        var p = new Pair();
        p.A.SetMyOffer(Offer("Steel", 100));
        p.B.SetMyOffer(Offer("Gold", 5));
        p.Flush();
        p.B.SetAccepted(true);
        p.Flush();
        Assert.True(p.A.TheyAccepted);

        // A sneaks in a smaller offer: B's acceptance no longer counts.
        p.A.SetMyOffer(Offer("Steel", 1));
        p.Flush();
        Assert.False(p.A.TheyAccepted);
        Assert.False(p.B.IAccepted);

        p.A.SetAccepted(true);
        p.Flush();
        Assert.Equal(TradeState.Open, p.A.State);
        Assert.Equal(TradeState.Open, p.B.State);
        Assert.Equal(0, p.CompletedA + p.CompletedB);
    }

    [Fact]
    public void LastSecondChangeNeverCompletesOnlyOneSide()
    {
        var p = new Pair();
        p.A.SetMyOffer(Offer("Steel", 100));
        p.B.SetMyOffer(Offer("Gold", 5));
        p.Flush();
        p.A.SetAccepted(true);
        p.Flush();

        // B accepts and, before anything is delivered, also changes its offer; A only sees the accept first.
        p.B.SetAccepted(true);
        p.B.SetMyOffer(Offer("Gold", 1));
        // B is locked after its commit, so the change is refused...
        Assert.True(p.B.Locked);
        Assert.Equal(5, p.B.MyOffer.Single().Count);
        p.Flush();

        // ...and the trade completes on the original terms, on both sides.
        Assert.Equal(1, p.CompletedA);
        Assert.Equal(1, p.CompletedB);
        Assert.Equal(5, p.A.TheirOffer.Single().Count);
    }

    [Fact]
    public void CrossingChangeAndAcceptDoesNotComplete()
    {
        var p = new Pair();
        p.A.SetMyOffer(Offer("Steel", 100));
        p.B.SetMyOffer(Offer("Gold", 5));
        p.Flush();
        p.A.SetAccepted(true);
        p.Flush(); // B knows A accepted Gold x5

        // At the same moment: B accepts Steel x100, while A changes its offer to Steel x1.
        p.B.SetAccepted(true);   // B commits to (Gold5, Steel100)
        p.A.SetMyOffer(Offer("Steel", 1));
        p.Flush();

        // A's commit-pair would have to be (Steel1, Gold5) vs B's (Gold5, Steel100): no match, nobody hands over.
        Assert.Equal(0, p.CompletedA);
        Assert.Equal(0, p.CompletedB);

        // B sees the new offer, is unlocked and can decide again.
        Assert.False(p.B.Locked);
        p.B.SetAccepted(true);
        p.Flush();
        Assert.Equal(1, p.CompletedA);
        Assert.Equal(1, p.CompletedB);
        Assert.Equal(1, p.B.TheirOffer.Single().Count);
    }

    [Fact]
    public void CancelEndsBothSidesButNotAfterCommitting()
    {
        var p = new Pair();
        p.A.Cancel();
        p.Flush();
        Assert.Equal(TradeState.Cancelled, p.B.State);

        var q = new Pair();
        q.A.SetMyOffer(Offer("Steel", 1));
        q.B.SetMyOffer(Offer("Gold", 1));
        q.Flush();
        q.B.SetAccepted(true);
        q.Flush();
        q.A.SetAccepted(true); // A commits; B's commit is still in flight
        q.A.Cancel();          // refused: B may already be handing over
        Assert.True(q.A.Locked);
        q.Flush();
        Assert.Equal(1, q.CompletedA);
        Assert.Equal(1, q.CompletedB);
    }

    [Fact]
    public void TradeMessagesRoundTrip()
    {
        var message = new TradeMessage { Kind = TradeMessageKind.Commit, Version = 3, OtherVersion = 7, CaravanId = 42, Lines = Offer("Сталь", 200, 380.5f) };
        var copy = TradeMessage.Decode(message.Encode());
        Assert.Equal(42, copy.CaravanId);
        Assert.Equal(TradeMessageKind.Commit, copy.Kind);
        Assert.Equal(3, copy.Version);
        Assert.Equal(7, copy.OtherVersion);
        Assert.Equal("Сталь", copy.Lines.Single().Label);
        Assert.Equal(380.5f, copy.Lines.Single().Value);
    }

    // ---- over a real server ----

    private sealed class Host
    {
        public readonly TransportHub Hub = new();
        public readonly GameServer Server;
        public readonly LoopbackEndpoint Loopback;
        private double _time;

        public Host()
        {
            Server = new GameServer(new ServerSettings(), Hub, world: new WorldState { Definition = new WorldDefinition { WorldId = "w" } });
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
            for (var i = 0; i < 30; i++)
            {
                Hub.Poll();
                Server.Update(_time += 1);
                foreach (var session in sessions)
                    session.Poll();
            }
        }
    }

    [Fact]
    public void TradeWorksThroughTheServer()
    {
        var host = new Host();
        var a = host.Join("A", 1);
        var b = host.Join("B", 2);
        var tradesA = new TradeManager(a);
        var tradesB = new TradeManager(b);
        PlayerTrade? invited = null;
        tradesB.Invited += t => invited = t;
        host.Pump(a, b);
        a.EnterWorld("w");
        b.EnterWorld("w");
        host.Pump(a, b);

        var bId = a.Players.Single(p => p.Name == "B").Id;
        var trade = tradesA.Invite(bId)!;
        host.Pump(a, b);
        Assert.NotNull(invited);
        Assert.Equal("A", invited!.PartnerName);

        invited.Join();
        host.Pump(a, b);
        Assert.Equal(TradeState.Open, trade.State);

        var completed = 0;
        trade.Completed += () => completed++;
        invited.Completed += () => completed++;
        trade.SetMyOffer(Offer("Steel", 50));
        invited.SetMyOffer(Offer("Medicine", 3));
        host.Pump(a, b);
        trade.SetAccepted(true);
        invited.SetAccepted(true);
        host.Pump(a, b);

        Assert.Equal(2, completed);
        Assert.False(tradesA.Busy);
    }

    [Fact]
    public void SecondInviteIsTurnedDownWhileTrading()
    {
        var host = new Host();
        var a = host.Join("A", 1);
        var b = host.Join("B", 2);
        var c = host.Join("C", 3);
        var ta = new TradeManager(a);
        var tb = new TradeManager(b);
        var tc = new TradeManager(c);
        tb.Invited += t => t.Join();
        host.Pump(a, b, c);
        foreach (var s in new[] { a, b, c })
            s.EnterWorld("w");
        host.Pump(a, b, c);

        var bId = a.Players.Single(p => p.Name == "B").Id;
        ta.Invite(bId);
        host.Pump(a, b, c);
        var late = tc.Invite(bId)!;
        host.Pump(a, b, c);

        Assert.Equal(TradeState.Cancelled, late.State);
        Assert.Equal(TradeState.Open, ta.Current!.State);
    }

    [Fact]
    public void PartnerLeavingTheWorldAbortsTheTrade()
    {
        var host = new Host();
        var a = host.Join("A", 1);
        var b = host.Join("B", 2);
        var ta = new TradeManager(a);
        var tb = new TradeManager(b);
        tb.Invited += t => t.Join();
        host.Pump(a, b);
        a.EnterWorld("w");
        b.EnterWorld("w");
        host.Pump(a, b);
        var trade = ta.Invite(a.Players.Single(p => p.Name == "B").Id)!;
        host.Pump(a, b);

        b.LeaveWorld();
        host.Pump(a, b);
        Assert.Equal(TradeState.Cancelled, trade.State);
    }

    [Fact]
    public void RelayOnlyWorksBetweenPlayersInTheWorld()
    {
        var host = new Host();
        var a = host.Join("A", 1);
        var b = host.Join("B", 2);
        var received = 0;
        b.RelayReceived += (_, _, _) => received++;
        host.Pump(a, b);

        var bId = a.Players.Single(p => p.Name == "B").Id;
        a.SendRelay(bId, RelayChannel.Trade, [1]);
        host.Pump(a, b);
        Assert.Equal(0, received);

        a.EnterWorld("w");
        b.EnterWorld("w");
        host.Pump(a, b);
        a.SendRelay(bId, RelayChannel.Trade, [1]);
        host.Pump(a, b);
        Assert.Equal(1, received);
    }

    [Fact]
    public void CaravanTradeTellsThePartnerWhichCaravanToLoad()
    {
        var host = new Host();
        var a = host.Join("A", 1);
        var b = host.Join("B", 2);
        var ta = new TradeManager(a);
        var tb = new TradeManager(b);
        PlayerTrade? invited = null;
        tb.Invited += t => invited = t;
        host.Pump(a, b);
        a.EnterWorld("w");
        b.EnterWorld("w");
        host.Pump(a, b);

        var trade = ta.Invite(a.Players.Single(p => p.Name == "B").Id, caravanId: 1234)!;
        host.Pump(a, b);

        Assert.Equal(1234, trade.MyCaravanId);
        Assert.Equal(1234, invited!.PartnerCaravanId);
        Assert.Equal(0, invited.MyCaravanId);
    }

    [Theory]
    [InlineData("1234", null, "1234")]
    [InlineData("caravan:77|1234", 77, "1234")]
    [InlineData("caravan:77|", 77, "")]
    [InlineData("caravan:x|1234", null, "1234")]
    public void ParcelAddressesParse(string address, int? caravan, string tile)
    {
        ParcelAddress.Parse(address, out var parsedCaravan, out var parsedTile);
        Assert.Equal(caravan, parsedCaravan);
        Assert.Equal(tile, parsedTile);
    }

    [Fact]
    public void CaravanAddressRoundTrips()
    {
        ParcelAddress.Parse(ParcelAddress.ForCaravan(5, "99@1"), out var caravan, out var tile);
        Assert.Equal(5, caravan);
        Assert.Equal("99@1", tile);
    }
}
