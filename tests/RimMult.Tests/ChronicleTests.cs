using RimMult.ClientCore;
using RimMult.ServerCore;
using RimMult.Shared.Mods;
using RimMult.Shared.Packets;
using RimMult.Shared.Serialization;
using RimMult.Shared.World;

namespace RimMult.Tests;

public class ChronicleTests
{
    private sealed class Host
    {
        public readonly TransportHub Hub = new();
        public readonly GameServer Server;
        public readonly LoopbackEndpoint Loopback;
        private double _time;

        public Host(WorldState? world = null, ServerSettings? settings = null)
        {
            Server = new GameServer(settings ?? new ServerSettings(), Hub, world: world);
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

    private static WorldState World() => new() { Definition = new WorldDefinition { WorldId = "w", SeedString = "s" }, Tick = 1000 };

    private static (Host Host, ClientSession A, ClientSession B) TwoInWorld(WorldState? world = null)
    {
        var host = new Host(world ?? World());
        var a = host.Join("A", 1);
        var b = host.Join("B", 2);
        host.Pump(a, b);
        a.EnterWorld("w");
        b.EnterWorld("w");
        host.Pump(a, b);
        return (host, a, b);
    }

    [Fact]
    public void EntriesAndStatsRoundTrip()
    {
        var update = new ChronicleUpdate
        {
            Full = true,
            Entries =
            [
                new ChronicleEntry { Tick = 3_600_000, UnixTime = 1_791_000_000, Kind = ChronicleKind.RaidEnded, Actor = 7, ActorName = "Ваня", Target = 9, TargetName = "Друг", A = 3, B = 1, C = 2, Text = "x" },
            ],
            Stats = [new PlayerStats { Owner = 7, Name = "Ваня", ColorIndex = 2, Colonies = 1, Wealth = 12345.5f, Colonists = 8, RaidsLed = 4, RaidsSuffered = 1, HelpsSent = 2, ParcelsSent = 5, SettlementsDestroyed = 3 }],
        };

        var copy = Assert.IsType<ChronicleUpdate>(PacketCodec.Decode(PacketCodec.Encode(update)));

        Assert.True(copy.Full);
        var entry = Assert.Single(copy.Entries);
        Assert.Equal((3_600_000L, 1_791_000_000L, ChronicleKind.RaidEnded, 7UL, "Ваня", 9UL, "Друг", 3, 1, 2, "x"),
            (entry.Tick, entry.UnixTime, entry.Kind, entry.Actor, entry.ActorName, entry.Target, entry.TargetName, entry.A, entry.B, entry.C, entry.Text));
        var stats = Assert.Single(copy.Stats);
        Assert.Equal((7UL, "Ваня", (byte)2, 1, 12345.5f, 8, 4, 1, 2, 5, 3),
            (stats.Owner, stats.Name, stats.ColorIndex, stats.Colonies, stats.Wealth, stats.Colonists, stats.RaidsLed, stats.RaidsSuffered, stats.HelpsSent, stats.ParcelsSent, stats.SettlementsDestroyed));

        var report = Assert.IsType<ColonyStatsReport>(PacketCodec.Decode(PacketCodec.Encode(new ColonyStatsReport { Wealth = 5000f, Colonists = 6 })));
        Assert.Equal((5000f, 6), (report.Wealth, report.Colonists));
        Assert.Throws<ProtocolException>(() => PacketCodec.Decode(PacketCodec.Encode(new ColonyStatsReport { Wealth = float.NaN })));
    }

    [Fact]
    public void CreatingTheWorldArrivingAndFoundingAreRecorded()
    {
        var host = new Host();
        var a = host.Join("A", 1);
        var b = host.Join("B", 2);
        host.Pump(a, b);

        a.CreateWorld(new WorldDefinition { WorldId = "w", SeedString = "seed" }, 500);
        host.Pump(a, b);
        a.EnterWorld("w");
        host.Pump(a, b);
        a.SendColonies([new ColonyInfo { Name = "Home", Tile = "10" }]);
        host.Pump(a, b);
        // The same list again (reported every few seconds) is no news; an outpost is.
        a.SendColonies([new ColonyInfo { Name = "Home", Tile = "10" }]);
        a.SendColonies([new ColonyInfo { Name = "Home", Tile = "10" }, new ColonyInfo { Name = "Outpost", Tile = "77" }]);
        host.Pump(a, b);
        a.SendColonies([new ColonyInfo { Name = "Home", Tile = "10" }]);
        host.Pump(a, b);

        Assert.Equal(
            [ChronicleKind.WorldCreated, ChronicleKind.PlayerArrived, ChronicleKind.ColonyFounded, ChronicleKind.ColonyFounded, ChronicleKind.ColonyAbandoned],
            b.Chronicle.Select(e => e.Kind));
        Assert.Equal("seed", b.Chronicle[0].Text);
        Assert.Equal(("A", "Outpost"), (b.Chronicle[3].ActorName, b.Chronicle[3].Text));
        Assert.Equal("Outpost", b.Chronicle[4].Text);
        var stats = Assert.Single(b.Stats);
        Assert.Equal(("A", 1), (stats.Name, stats.Colonies));
    }

    [Fact]
    public void ArrivingAgainIsNotNews()
    {
        var (host, a, b) = TwoInWorld();
        a.LeaveWorld();
        host.Pump(a, b);
        a.EnterWorld("w");
        host.Pump(a, b);

        Assert.Equal(2, b.Chronicle.Count(e => e.Kind == ChronicleKind.PlayerArrived));
    }

    [Fact]
    public void WarRaidAndItsEndAreRecordedWithStats()
    {
        var (host, a, b) = TwoInWorld();
        var added = new List<ChronicleEntry>();
        b.ChronicleAdded += added.AddRange;

        a.SendDiplomacy(2, DiplomacyAction.DeclareWar);
        host.Pump(a, b);
        a.SendParcel(2, ParcelAddress.ForRaid("r1", ParcelAddress.RaidArrival.WalkIn, "20", live: true), "Bob, Ann", [1]);
        host.Pump(a, b);
        // The defender sends the survivors home: 1 back, 1 captive, 2 dead.
        b.SendParcel(1, ParcelAddress.ForRaidReturn("r1", "20", 1, 1), "D:Bob\nD:Cat\nL:Ann", [1]);
        host.Pump(a, b);

        Assert.Equal([ChronicleKind.WarDeclared, ChronicleKind.RaidLaunched, ChronicleKind.RaidEnded], added.Select(e => e.Kind));
        Assert.Equal((1UL, 2UL), (added[0].Actor, added[0].Target));
        Assert.Equal((1, "Bob, Ann"), (added[1].A, added[1].Text));
        var end = added[2];
        Assert.Equal((1UL, "A", 2UL, "B", 1, 1, 2), (end.Actor, end.ActorName, end.Target, end.TargetName, end.A, end.B, end.C));
        Assert.Equal(1, a.Stats.Single(s => s.Owner == 1).RaidsLed);
        Assert.Equal(1, a.Stats.Single(s => s.Owner == 2).RaidsSuffered);
    }

    [Fact]
    public void RefusedRaidIsNotRecorded()
    {
        var (host, a, b) = TwoInWorld();
        a.SendParcel(2, ParcelAddress.ForRaid("r1", ParcelAddress.RaidArrival.WalkIn, "20"), "Bob", [1]);
        host.Pump(a, b);

        Assert.DoesNotContain(b.Chronicle, e => e.Kind == ChronicleKind.RaidLaunched);
        Assert.Equal(0, a.Stats.Single(s => s.Owner == 1).RaidsLed);
    }

    [Fact]
    public void GiftsHelpAndSettlementsAreRecorded()
    {
        var world = World();
        world.Colonies.Add(new ColonyInfo { OwnerSteamId = 2, OwnerName = "B", Name = "Home B", Tile = "20" });
        var (host, a, b) = TwoInWorld(world);

        a.SendParcel(2, "20", "Steel x200", [1]);
        a.SendParcel(2, ParcelAddress.ForHelp("h1", ParcelAddress.RaidArrival.DropPods), "Bob", [1]);
        b.SendParcel(1, ParcelAddress.ForHelpReturn("h1"), "D:Bob", [1]);
        a.ReportSettlementDestroyed("123");
        host.Pump(a, b);

        Assert.Equal(
            [ChronicleKind.ParcelSent, ChronicleKind.HelpSent, ChronicleKind.HelpEnded, ChronicleKind.SettlementDestroyed],
            b.Chronicle.Where(e => e.Kind != ChronicleKind.PlayerArrived).Select(e => e.Kind));
        Assert.Equal("Steel x200", b.Chronicle.Single(e => e.Kind == ChronicleKind.ParcelSent).Text);
        Assert.Equal((1UL, 1), (b.Chronicle.Single(e => e.Kind == ChronicleKind.HelpEnded).Actor, b.Chronicle.Single(e => e.Kind == ChronicleKind.HelpEnded).C));
        var stats = a.Stats.Single(s => s.Owner == 1);
        Assert.Equal((1, 1, 1), (stats.ParcelsSent, stats.HelpsSent, stats.SettlementsDestroyed));
    }

    [Fact]
    public void ColonyStatsCountOnlyFromTheWorldAndOnlyWhenChanged()
    {
        var host = new Host(World());
        var a = host.Join("A", 1);
        var b = host.Join("B", 2);
        host.Pump(a, b);
        var changes = 0;
        b.ChronicleChanged += () => changes++;

        a.ReportColonyStats(5000f, 5);
        host.Pump(a, b);
        Assert.Equal(0, changes); // from the lobby: ignored

        a.EnterWorld("w");
        host.Pump(a, b);
        changes = 0;
        a.ReportColonyStats(5000.2f, 5);
        a.ReportColonyStats(5000.3f, 5);
        host.Pump(a, b);

        Assert.Equal(1, changes);
        Assert.Equal((5000f, 5), (b.Stats.Single().Wealth, b.Stats.Single().Colonists));
    }

    [Fact]
    public void LateJoinerGetsTheWholeChronicleAndItSurvivesTheWorldFile()
    {
        var world = World();
        var (host, a, b) = TwoInWorld(world);
        a.SendDiplomacy(2, DiplomacyAction.DeclareWar);
        host.Pump(a, b);

        var c = host.Join("C", 3);
        host.Pump(a, b, c);
        Assert.Equal(b.Chronicle.Select(e => e.Kind), c.Chronicle.Select(e => e.Kind));
        Assert.Equal(2, c.Stats.Count);

        var restored = WorldState.Deserialize(world.Serialize());
        Assert.Equal(world.Chronicle.Select(e => (e.Kind, e.Actor)), restored.Chronicle.Select(e => (e.Kind, e.Actor)));
        Assert.Equal("B", restored.Stats[2].Name);
    }

    [Fact]
    public void ChronicleKeepsOnlyTheNewestEntries()
    {
        var world = World();
        for (var i = 0; i < Chronicle.MaxEntries; i++)
            world.Chronicle.Add(new ChronicleEntry { Kind = ChronicleKind.ParcelSent, A = i });
        var (host, a, b) = TwoInWorld(world);

        Assert.Equal(Chronicle.MaxEntries, world.Chronicle.Count);
        Assert.Equal(ChronicleKind.PlayerArrived, world.Chronicle[^1].Kind);
        Assert.Equal(2, world.Chronicle[0].A); // the two oldest made room for the two arrivals
        Assert.Equal(Chronicle.MaxEntries, b.Chronicle.Count);
        host.Pump(a, b);
    }

    [Fact]
    public void Version4WorldFilesLoadWithoutAChronicle()
    {
        var current = World();
        var bytes = current.Serialize();
        // Format 4 is the current one without the chronicle, stats and NPC settlements (empty lists: a zero each)
        // and the creator (8 bytes), then format 7's treaties and format 8's market (a byte each) at the end.
        var old = bytes.Take(bytes.Length - 3 - 8 - 2 - 3).ToArray();
        old[0] = 4;

        var state = WorldState.Deserialize(old);
        Assert.Empty(state.Chronicle);
        Assert.Empty(state.Stats);
        Assert.Equal("w", state.Definition!.WorldId);
    }
}
