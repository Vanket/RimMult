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

        // Relations are part of the world: they survive a restart.
        a.SendDiplomacy(2, DiplomacyAction.DeclareWar);
        host.Pump(a, b, c);
        var restored = WorldState.Deserialize(world.Serialize());
        Assert.Equal(PlayerRelation.Hostile, RelationEntry.Between(restored.Relations, 1, 2));
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
        // Drop the (empty) relation list written by the current format: one varint zero at the end.
        foreach (var b in bodyBytes.Take(bodyBytes.Length - 1))
            writer.WriteByte(b);
        var old = WorldState.Deserialize(writer.ToArray());
        Assert.Empty(old.Relations);
        Assert.Equal(2, old.Colonies.Count);
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
}
