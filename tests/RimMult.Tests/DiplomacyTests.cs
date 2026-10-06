using RimMult.ClientCore;
using RimMult.ServerCore;
using RimMult.Shared.Mods;
using RimMult.Shared.Packets;
using RimMult.Shared.Serialization;
using RimMult.Shared.World;

namespace RimMult.Tests;

public class DiplomacyTests
{
    private sealed class Host
    {
        public readonly TransportHub Hub = new();
        public readonly GameServer Server;
        public readonly LoopbackEndpoint Loopback;
        private double _time;

        public Host(WorldState world, bool allowPvp = true)
        {
            Server = new GameServer(new ServerSettings { AllowPvp = allowPvp }, Hub, world: world);
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

    private static List<DiplomacyNotice> Notices(ClientSession session)
    {
        var notices = new List<DiplomacyNotice>();
        session.DiplomacyReceived += notices.Add;
        return notices;
    }

    private static List<MailItem> Inbox(ClientSession session)
    {
        var inbox = new List<MailItem>();
        session.ParcelReceived += inbox.Add;
        return inbox;
    }

    [Fact]
    public void WarIsOneSidedPeaceNeedsBoth()
    {
        var book = new DiplomacyBook([]);

        Assert.Equal(DiplomacyEvent.WarDeclared, book.Apply(1, 2, DiplomacyAction.DeclareWar, allowPvp: true));
        Assert.Equal(PlayerRelation.Hostile, book.Get(2, 1));

        // Accepting a peace nobody offered does nothing.
        Assert.Null(book.Apply(2, 1, DiplomacyAction.Accept, true));

        Assert.Equal(DiplomacyEvent.PeaceProposed, book.Apply(2, 1, DiplomacyAction.ProposePeace, true));
        Assert.Null(book.Apply(2, 1, DiplomacyAction.ProposePeace, true)); // asked twice: nothing new
        Assert.Equal(PlayerRelation.Hostile, book.Get(1, 2));

        Assert.Equal(DiplomacyEvent.PeaceMade, book.Apply(1, 2, DiplomacyAction.Accept, true));
        Assert.Equal(PlayerRelation.Neutral, book.Get(1, 2));
    }

    [Fact]
    public void AlliesMustBreakTheAllianceBeforeWar()
    {
        var book = new DiplomacyBook([]);
        Assert.Equal(DiplomacyEvent.AllianceProposed, book.Apply(1, 2, DiplomacyAction.ProposeAlliance, true));
        Assert.Equal(DiplomacyEvent.AllianceMade, book.Apply(2, 1, DiplomacyAction.Accept, true));
        Assert.Equal(PlayerRelation.Allied, book.Get(1, 2));

        Assert.Null(book.Apply(1, 2, DiplomacyAction.DeclareWar, true));

        Assert.Equal(DiplomacyEvent.AllianceBroken, book.Apply(2, 1, DiplomacyAction.BreakAlliance, true));
        Assert.Equal(DiplomacyEvent.WarDeclared, book.Apply(1, 2, DiplomacyAction.DeclareWar, true));
    }

    [Fact]
    public void DeclinedProposalIsGoneAndNoWarWithoutPvp()
    {
        var book = new DiplomacyBook([]);
        Assert.Null(book.Apply(1, 2, DiplomacyAction.DeclareWar, allowPvp: false));
        Assert.Null(book.Apply(1, 1, DiplomacyAction.ProposeAlliance, true));

        book.Apply(1, 2, DiplomacyAction.ProposeAlliance, true);
        Assert.Equal(DiplomacyEvent.ProposalDeclined, book.Apply(2, 1, DiplomacyAction.Decline, true));
        Assert.Null(book.Apply(2, 1, DiplomacyAction.Accept, true));
        Assert.Equal(PlayerRelation.Neutral, book.Get(1, 2));
    }

    [Fact]
    public void ServerAnnouncesWarAndRelaysPeace()
    {
        var world = World();
        var host = new Host(world);
        var a = host.Join("A", 1);
        var b = host.Join("B", 2);
        var c = host.Join("C", 3);
        host.Pump(a, b, c);
        var atA = Notices(a);
        var atB = Notices(b);
        var atC = Notices(c);
        Assert.True(a.AllowPvp);

        a.SendDiplomacy(2, DiplomacyAction.DeclareWar);
        host.Pump(a, b, c);

        // Everyone hears about a war and sees the new relation.
        Assert.All(new[] { atA, atB, atC }, n => Assert.Equal(DiplomacyEvent.WarDeclared, Assert.Single(n).Event));
        Assert.Equal(("A", "B"), (atC[0].FromName, atC[0].ToName));
        Assert.Equal(PlayerRelation.Hostile, a.RelationWith(2));
        Assert.Equal(PlayerRelation.Hostile, b.RelationWith(1));
        Assert.Equal(PlayerRelation.Neutral, c.RelationWith(1));

        // A peace offer goes only to the one it is for.
        b.SendDiplomacy(1, DiplomacyAction.ProposePeace);
        host.Pump(a, b, c);
        Assert.Equal(DiplomacyEvent.PeaceProposed, atA.Last().Event);
        Assert.Single(atB);
        Assert.Single(atC);

        a.SendDiplomacy(2, DiplomacyAction.Accept);
        host.Pump(a, b, c);
        Assert.Equal(DiplomacyEvent.PeaceMade, atC.Last().Event);
        Assert.Equal(PlayerRelation.Neutral, b.RelationWith(1));

        // A truce follows the peace: no war right away, unless someone tears it up (for all to see).
        Assert.Equal(TreatyKind.Truce, Assert.Single(a.TreatiesWith(2)).Kind);
        a.SendDiplomacy(2, DiplomacyAction.DeclareWar);
        host.Pump(a, b, c);
        Assert.Equal(PlayerRelation.Neutral, a.RelationWith(2));
        a.SendDiplomacy(2, DiplomacyAction.BreakTreaty);
        host.Pump(a, b, c);
        Assert.Equal(DiplomacyEvent.TreatyBroken, atC.Last().Event);
        Assert.Equal(1, Assert.Single(c.Stats, s => s.Owner == 1).TreatiesBroken);

        // Relations are part of the world: they survive a restart.
        a.SendDiplomacy(2, DiplomacyAction.DeclareWar);
        host.Pump(a, b, c);
        var restored = WorldState.Deserialize(world.Serialize());
        Assert.Equal(PlayerRelation.Hostile, RelationEntry.Between(restored.Relations, 1, 2));
        Assert.Equal(2, restored.Stats[1].WarsDeclared);
        Assert.Equal(1, restored.Stats[1].TreatiesBroken);
    }

    [Fact]
    public void PactForbidsWarUntilItEndsAndKeepingItCounts()
    {
        var treaties = new List<Treaty>();
        var book = new DiplomacyBook([], treaties);

        Assert.Equal(DiplomacyEvent.PactProposed, book.Apply(1, 2, DiplomacyAction.ProposePact, true, 0, new TreatyTerms { Days = 10 }));
        Assert.Equal(DiplomacyEvent.PactMade, book.Apply(2, 1, DiplomacyAction.Accept, true, 100));
        var pact = Assert.Single(treaties);
        Assert.Equal((TreatyKind.Pact, 100L, 100L + 10 * Treaty.TicksPerDay), (pact.Kind, pact.StartTick, pact.EndTick));

        // No war while it runs, nor a second pact.
        Assert.Null(book.Apply(1, 2, DiplomacyAction.DeclareWar, true, 5000));
        Assert.Null(book.Apply(2, 1, DiplomacyAction.ProposePact, true, 5000, new TreatyTerms { Days = 5 }));

        // It runs out: war is possible again.
        Assert.Empty(book.Expire(pact.EndTick - 1));
        Assert.Same(pact, Assert.Single(book.Expire(pact.EndTick)));
        Assert.Equal(DiplomacyEvent.WarDeclared, book.Apply(1, 2, DiplomacyAction.DeclareWar, true, pact.EndTick));
    }

    [Fact]
    public void UltimatumMeansTributeOrWar()
    {
        var treaties = new List<Treaty>();
        var book = new DiplomacyBook([], treaties);
        var terms = new TreatyTerms { Days = 30, Tribute = 500 };

        // Accepted: the one who accepts pays, starting now, and there's no war meanwhile.
        Assert.Equal(DiplomacyEvent.TributeDemanded, book.Apply(1, 2, DiplomacyAction.DemandTribute, true, 0, terms));
        Assert.Equal(DiplomacyEvent.TributeAgreed, book.Apply(2, 1, DiplomacyAction.Accept, true, 1000));
        var tribute = Assert.Single(treaties);
        Assert.Equal((2UL, 1UL, 500), (tribute.Payer, tribute.Receiver, tribute.Amount));
        Assert.True(tribute.PaymentDue(1000));
        Assert.Null(book.Apply(1, 2, DiplomacyAction.DeclareWar, true, 2000));

        // Too little doesn't count; the full sum moves the next payment a quadrum on.
        Assert.Null(book.Paid(tribute.Id, 2, 499));
        Assert.Null(book.Paid(tribute.Id, 1, 500));
        Assert.Same(tribute, book.Paid(tribute.Id, 2, 500));
        Assert.False(tribute.PaymentDue(2000));
        Assert.True(tribute.PaymentDue(1000 + Treaty.TributeIntervalTicks));

        // Turned down: war.
        var other = new DiplomacyBook([]);
        other.Apply(1, 3, DiplomacyAction.DemandTribute, true, 0, terms);
        Assert.Equal(DiplomacyEvent.UltimatumRejected, other.Apply(3, 1, DiplomacyAction.Decline, true));
        Assert.Equal(PlayerRelation.Hostile, other.Get(1, 3));

        // Without PvP there are no threats.
        Assert.Null(new DiplomacyBook([]).Apply(1, 2, DiplomacyAction.DemandTribute, false, 0, terms));
    }

    [Fact]
    public void PeaceCanBeBoughtWithATribute()
    {
        var treaties = new List<Treaty>();
        var book = new DiplomacyBook([new RelationEntry(1, 2, PlayerRelation.Hostile)], treaties);

        Assert.Equal(DiplomacyEvent.PeaceProposed, book.Apply(1, 2, DiplomacyAction.ProposePeace, true, 0,
            new TreatyTerms { Days = 45, Tribute = 300, ProposerPays = true }));
        Assert.Equal(300, book.LastTerms.Tribute);
        Assert.Equal(DiplomacyEvent.PeaceMade, book.Apply(2, 1, DiplomacyAction.Accept, true, 0));

        var tribute = Assert.Single(treaties);
        Assert.Equal((TreatyKind.Tribute, 1UL, 300), (tribute.Kind, tribute.Payer, tribute.Amount));
        Assert.Equal(45L * Treaty.TicksPerDay, tribute.EndTick);
        Assert.Equal(PlayerRelation.Neutral, book.Get(1, 2));
    }

    [Fact]
    public void ServerAsksForTributeAndRecordsThePayment()
    {
        var world = World();
        var host = new Host(world);
        var a = host.Join("A", 1);
        var b = host.Join("B", 2);
        host.Pump(a, b);
        a.EnterWorld("w");
        b.EnterWorld("w");
        host.Pump(a, b);
        var atA = Notices(a);
        var atB = Notices(b);
        var inboxB = Inbox(b);

        a.SendDiplomacy(2, DiplomacyAction.DemandTribute, new TreatyTerms { Days = 30, Tribute = 200 });
        host.Pump(a, b);
        var demand = Assert.Single(atB);
        Assert.Equal((DiplomacyEvent.TributeDemanded, 200, 30), (demand.Event, demand.Terms.Tribute, demand.Terms.Days));

        b.SendDiplomacy(1, DiplomacyAction.Accept);
        host.Pump(a, b);
        Assert.Contains(atA, n => n.Event == DiplomacyEvent.TributeAgreed);
        // The first payment is due at once, and only the payer is asked (once).
        var due = Assert.Single(atB, n => n.Event == DiplomacyEvent.TributeDue);
        Assert.DoesNotContain(atA, n => n.Event == DiplomacyEvent.TributeDue);
        Assert.Equal(200, due.Terms.Tribute);

        b.SendParcel(1, ParcelAddress.ForTribute(due.TreatyId, 200), "Silver x200", [1]);
        host.Pump(a, b);
        Assert.Contains(atA, n => n.Event == DiplomacyEvent.TributePaid);
        Assert.Equal(1, Assert.Single(world.Treaties).Payments);
        Assert.Equal(200, world.Stats[2].TributePaid);
        Assert.Contains(world.Chronicle, e => e.Kind == ChronicleKind.TributePaid && e.A == 200);
        Assert.Empty(inboxB);

        // When it runs out, both kept their word.
        world.Tick += 31L * Treaty.TicksPerDay;
        host.Pump(a, b);
        Assert.Empty(world.Treaties);
        Assert.Equal((1, 1), (world.Stats[1].TreatiesKept, world.Stats[2].TreatiesKept));
        Assert.Contains(atB, n => n.Event == DiplomacyEvent.TreatyExpired);
    }

    [Fact]
    public void AllyChatAndWhispersReachOnlyThoseMeant()
    {
        var host = new Host(World());
        var a = host.Join("A", 1);
        var b = host.Join("B", 2);
        var c = host.Join("C", 3);
        host.Pump(a, b, c);
        a.SendDiplomacy(2, DiplomacyAction.ProposeAlliance);
        host.Pump(a, b, c);
        b.SendDiplomacy(1, DiplomacyAction.Accept);
        host.Pump(a, b, c);

        a.SendChat("to allies", ChatScope.Allies);
        host.Pump(a, b, c);
        Assert.Equal(ChatScope.Allies, a.Chat.Last().Scope);
        Assert.Equal(("A", ChatScope.Allies), (b.Chat.Last().SenderName, b.Chat.Last().Scope));
        Assert.DoesNotContain(c.Chat, l => l.Text == "to allies");

        var cId = c.PlayerId;
        b.SendChat("psst", ChatScope.Whisper, cId);
        host.Pump(a, b, c);
        Assert.Equal(("psst", ChatScope.Whisper, "C"), (c.Chat.Last().Text, c.Chat.Last().Scope, c.Chat.Last().TargetName));
        Assert.Equal("psst", b.Chat.Last().Text); // the sender sees it too
        Assert.DoesNotContain(a.Chat, l => l.Text == "psst");

        // Nobody to hear it: the server says so to the sender only.
        c.SendChat("anyone?", ChatScope.Allies);
        host.Pump(a, b, c);
        Assert.Equal(ClientSession.ServerSenderName, c.Chat.Last().SenderName);
        Assert.DoesNotContain(a.Chat, l => l.Text == "anyone?");
    }

    [Fact]
    public void ResearchGoesOnlyToAllies()
    {
        var world = World();
        var host = new Host(world);
        var a = host.Join("A", 1);
        var b = host.Join("B", 2);
        host.Pump(a, b);
        a.EnterWorld("w");
        b.EnterWorld("w");
        host.Pump(a, b);
        var inboxB = Inbox(b);
        var address = ParcelAddress.ForResearch("Hydroponics", 400);

        a.SendParcel(2, address, "Hydroponics", []);
        host.Pump(a, b);
        Assert.Empty(inboxB);

        a.SendDiplomacy(2, DiplomacyAction.ProposeAlliance);
        host.Pump(a, b);
        b.SendDiplomacy(1, DiplomacyAction.Accept);
        host.Pump(a, b);
        a.SendParcel(2, address, "Hydroponics", []);
        host.Pump(a, b);

        Assert.True(ParcelAddress.TryParseResearch(Assert.Single(inboxB).ToTile, out var project, out var points));
        Assert.Equal(("Hydroponics", 400), (project, points));
        Assert.Contains(world.Chronicle, e => e.Kind == ChronicleKind.ResearchShared && e.A == 400 && e.Text == "Hydroponics");
    }

    [Fact]
    public void NoWarWhenTheServerForbidsPvp()
    {
        var host = new Host(World(), allowPvp: false);
        var a = host.Join("A", 1);
        var b = host.Join("B", 2);
        host.Pump(a, b);
        var atB = Notices(b);
        Assert.False(a.AllowPvp);

        a.SendDiplomacy(2, DiplomacyAction.DeclareWar);
        host.Pump(a, b);

        Assert.Empty(atB);
        Assert.Equal(PlayerRelation.Neutral, a.RelationWith(2));
    }

    [Fact]
    public void RaidReachesAnEnemyInTheWorld()
    {
        var host = new Host(World());
        var a = host.Join("A", 1);
        var b = host.Join("B", 2);
        host.Pump(a, b);
        a.EnterWorld("w");
        b.EnterWorld("w");
        a.SendDiplomacy(2, DiplomacyAction.DeclareWar);
        host.Pump(a, b);
        var inboxA = Inbox(a);
        var inboxB = Inbox(b);

        var address = ParcelAddress.ForRaid("r1", ParcelAddress.RaidArrival.DropPods, "20");
        a.SendParcel(2, address, "3 raiders", [1]);
        host.Pump(a, b);

        var raid = Assert.Single(inboxB);
        Assert.False(raid.Returned);
        Assert.Equal(1UL, raid.FromOwner);
        Assert.True(ParcelAddress.TryParseRaid(raid.ToTile, out var id, out var arrival, out var tile));
        Assert.Equal(("r1", ParcelAddress.RaidArrival.DropPods, "20"), (id, arrival, tile));
        Assert.Empty(inboxA);
    }

    [Fact]
    public void RaidWithoutWarOrOnAnAbsentPlayerComesBack()
    {
        var host = new Host(World());
        var a = host.Join("A", 1);
        var b = host.Join("B", 2);
        host.Pump(a, b);
        a.EnterWorld("w");
        b.EnterWorld("w");
        host.Pump(a, b);
        var inboxA = Inbox(a);
        var inboxB = Inbox(b);
        var address = ParcelAddress.ForRaid("r1", ParcelAddress.RaidArrival.WalkIn, "20");

        // Not at war.
        a.SendParcel(2, address, "raiders", [1]);
        host.Pump(a, b);
        var back = Assert.Single(inboxA);
        Assert.True(back.Returned);
        Assert.True(ParcelAddress.IsRaid(back.ToTile)); // the sender knows it was a raid that didn't happen
        Assert.Empty(inboxB);

        // At war, but the defender left the world.
        a.SendDiplomacy(2, DiplomacyAction.DeclareWar);
        b.LeaveWorld();
        host.Pump(a, b);
        a.SendParcel(2, address, "raiders", [1]);
        host.Pump(a, b);
        Assert.Equal(2, inboxA.Count);
        Assert.True(inboxA[1].Returned);
        Assert.Empty(inboxB);
    }

    [Fact]
    public void RaidersGoHomeEvenToAPlayerWithoutColonies()
    {
        var world = World();
        world.Colonies.RemoveAll(c => c.OwnerSteamId == 1); // the attacker sent everyone out
        var host = new Host(world);
        var a = host.Join("A", 1);
        var b = host.Join("B", 2);
        host.Pump(a, b);
        a.EnterWorld("w");
        b.EnterWorld("w");
        host.Pump(a, b);
        var inboxA = Inbox(a);
        var inboxB = Inbox(b);

        b.SendParcel(1, ParcelAddress.ForRaidReturn("r1", "20", 2, 1), "back", [1]);
        host.Pump(a, b);

        var home = Assert.Single(inboxA);
        Assert.False(home.Returned);
        Assert.True(ParcelAddress.TryParseRaidReturn(home.ToTile, out var id, out var tile, out var survivors, out var captives));
        Assert.Equal(("r1", "20", 2, 1), (id, tile, survivors, captives));
        Assert.Empty(inboxB);
    }

    [Fact]
    public void Version3WorldFilesLoadWithoutRelations()
    {
        var current = World();
        current.Relations.Add(new RelationEntry(2, 1, PlayerRelation.Allied));
        var bytes = current.Serialize();
        Assert.Equal(PlayerRelation.Allied, RelationEntry.Between(WorldState.Deserialize(bytes).Relations, 1, 2));

        // Same file as format 3: version byte 3 and no relations section at the end.
        var writer = new ByteWriter();
        writer.WriteByte(3);
        var body = new ByteWriter();
        World().Write(body);
        var bodyBytes = body.ToArray();
        // Drop what the current format writes after format 3's data: the (empty) relation list, chronicle, stats and
        // NPC settlements (one varint zero each), the creator (8 bytes), the treaties, the market and the prices (nine bytes).
        foreach (var b in bodyBytes.Take(bodyBytes.Length - 4 - 8 - 2 - 3 - 4))
            writer.WriteByte(b);
        var old = WorldState.Deserialize(writer.ToArray());
        Assert.Empty(old.Relations);
        Assert.Equal(2, old.Colonies.Count);
    }

    [Fact]
    public void MissilesReachOnlyAnEnemyInTheWorld()
    {
        var world = World();
        var host = new Host(world);
        var a = host.Join("A", 1);
        var b = host.Join("B", 2);
        host.Pump(a, b);
        a.EnterWorld("w");
        b.EnterWorld("w");
        host.Pump(a, b);
        var inboxA = Inbox(a);
        var inboxB = Inbox(b);
        var address = ParcelAddress.ForMissile("IRBM_Body", "IRBM_Warhead", "12");
        Assert.True(ParcelAddress.TryParseMissile(address, out var body, out var warhead, out var from));
        Assert.Equal(("IRBM_Body", "IRBM_Warhead", "12"), (body, warhead, from));

        // Not at war: it comes back to its sender as news that it hit nothing.
        a.SendParcel(2, address, "missile", []);
        host.Pump(a, b);
        Assert.True(Assert.Single(inboxA).Returned);
        Assert.Empty(inboxB);

        a.SendDiplomacy(2, DiplomacyAction.DeclareWar);
        host.Pump(a, b);
        a.SendParcel(2, address, "missile", []);
        host.Pump(a, b);
        Assert.False(Assert.Single(inboxB).Returned);
        Assert.Contains(world.Chronicle, e => e.Kind == ChronicleKind.MissileStrike && e.Actor == 1 && e.Target == 2);
        Assert.DoesNotContain(world.Chronicle, e => e.Kind == ChronicleKind.ParcelSent);
    }

    [Fact]
    public void LiveRaidAddressSaysSo()
    {
        var live = ParcelAddress.ForRaid("r2", ParcelAddress.RaidArrival.WalkIn, "31", live: true);
        Assert.True(ParcelAddress.TryParseRaid(live, out var id, out var arrival, out var tile, out var isLive));
        Assert.Equal(("r2", ParcelAddress.RaidArrival.WalkIn, "31", true), (id, arrival, tile, isLive));

        Assert.True(ParcelAddress.TryParseRaid(ParcelAddress.ForRaid("r3", ParcelAddress.RaidArrival.DropPods, "5"), out _, out _, out _, out isLive));
        Assert.False(isLive);
    }

    [Fact]
    public void HelpAddressesRoundTrip()
    {
        Assert.True(ParcelAddress.TryParseHelp(ParcelAddress.ForHelp("h1", ParcelAddress.RaidArrival.DropPods), out var id, out var arrival));
        Assert.Equal(("h1", ParcelAddress.RaidArrival.DropPods), (id, arrival));
        Assert.False(ParcelAddress.IsRaid(ParcelAddress.ForHelp("h1", ParcelAddress.RaidArrival.WalkIn)));
        Assert.True(ParcelAddress.IsHelpReturn(ParcelAddress.ForHelpReturn("h1")));
        Assert.False(ParcelAddress.IsHelp(ParcelAddress.ForHelpReturn("h1")));
    }
}
