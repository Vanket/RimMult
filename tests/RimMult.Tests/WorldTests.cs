using RimMult.ClientCore;
using RimMult.ServerCore;
using RimMult.Shared.Mods;
using RimMult.Shared.Packets;
using RimMult.Shared.Time;
using RimMult.Shared.World;

namespace RimMult.Tests;

public class WorldTests
{
    private sealed class Host
    {
        public readonly TransportHub Hub = new();
        public readonly GameServer Server;
        public readonly LoopbackEndpoint Loopback;
        public int WorldChanges;
        private double _time;

        public Host(WorldState? world = null, ServerSettings? settings = null)
        {
            Server = new GameServer(settings ?? new ServerSettings(), Hub, world: world);
            Server.WorldChanged += () => WorldChanges++;
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

    private static WorldDefinition Planet(string id = "world-1") => new()
    {
        WorldId = id,
        SeedString = "rimmult",
        PlanetCoverage = 0.3f,
        Rainfall = 3,
        Temperature = 3,
        Population = 3,
        LandmarkDensity = 2,
        Pollution = 0.05f,
        Factions = ["OutlanderCivil", "TribeRough", "TribeRough"],
    };

    [Fact]
    public void FirstCreatorDefinesTheWorldForEveryone()
    {
        var host = new Host();
        var a = host.Join("A", 1);
        var b = host.Join("B", 2);
        host.Pump(a, b);
        Assert.Null(b.World);
        var changesBefore = host.WorldChanges; // joining already assigned player colors

        a.CreateWorld(Planet(), tick: 900_000);
        host.Pump(a, b);

        Assert.Equal("rimmult", b.World?.SeedString);
        Assert.Equal(["OutlanderCivil", "TribeRough", "TribeRough"], b.World!.Factions);
        Assert.Equal(900_000, host.Server.World.Tick);
        Assert.True(host.WorldChanges > changesBefore);
        var changesAfter = host.WorldChanges;

        // A second creator does not replace it.
        b.CreateWorld(Planet("other"), tick: 5);
        host.Pump(a, b);
        Assert.Equal(changesAfter, host.WorldChanges);
        Assert.Equal("world-1", host.Server.World.Definition!.WorldId);
        Assert.Equal("world-1", b.World.WorldId);
    }

    [Fact]
    public void LateJoinerReceivesExistingWorld()
    {
        var host = new Host(new WorldState { Definition = Planet(), Tick = 123 });
        var a = host.Join("A", 1);
        host.Pump(a);
        Assert.Equal("world-1", a.World?.WorldId);
    }

    [Fact]
    public void EnteringTheWorldAlignsToTheSlowestColony()
    {
        var host = new Host(new WorldState { Definition = Planet(), Tick = 1000 });
        var a = host.Join("A", 1);
        var b = host.Join("B", 2);
        host.Pump(a, b);

        long? clockA = null, clockB = null;
        a.ClockReceived += tick => clockA = tick;
        b.ClockReceived += tick => clockB = tick;

        a.EnterWorld("world-1");
        host.Pump(a, b);
        Assert.Equal(1000, clockA);
        Assert.True(a.Players.Single(p => p.Name == "A").InWorld);

        a.ReportAuthority(5000, 1000);
        host.Pump(a, b);

        b.EnterWorld("world-1");
        host.Pump(a, b);
        Assert.Equal(5000, clockB);
        Assert.Equal(5000, host.Server.World.Tick);
    }

    [Fact]
    public void EnteringAnUnknownWorldIsRefused()
    {
        var host = new Host(new WorldState { Definition = Planet(), Tick = 1000 });
        var a = host.Join("A", 1);
        host.Pump(a);

        var clockReceived = false;
        a.ClockReceived += _ => clockReceived = true;
        a.EnterWorld("some-other-save");
        host.Pump(a);

        Assert.False(clockReceived);
        Assert.False(a.Players.Single().InWorld);
    }

    [Fact]
    public void LobbyClientsDoNotDriveTime()
    {
        var host = new Host(new WorldState { Definition = Planet(), Tick = 1000 });
        var a = host.Join("A", 1);
        host.Pump(a);

        a.ReportAuthority(99_999, 1000);
        host.Pump(a);

        Assert.False(host.Server.Time.HasAuthorities);
        Assert.Equal(1000, host.Server.World.Tick);
    }

    [Fact]
    public void ColoniesAreSharedAndSurviveReconnects()
    {
        var host = new Host(new WorldState { Definition = Planet(), Tick = 1000 });
        var a = host.Join("A", 1);
        var b = host.Join("B", 2);
        host.Pump(a, b);
        a.EnterWorld("world-1");
        host.Pump(a, b);

        a.SendColonies([new ColonyInfo { Name = "New Arrivals", Tile = "1234" }, new ColonyInfo { Name = "Outpost", Tile = "77" }]);
        host.Pump(a, b);

        Assert.Equal(2, b.Colonies.Count);
        Assert.All(b.Colonies, c => Assert.Equal("A", c.OwnerName));
        Assert.All(b.Colonies, c => Assert.Equal(1UL, c.OwnerSteamId));

        // Abandoning the outpost replaces the list.
        a.SendColonies([new ColonyInfo { Name = "New Arrivals", Tile = "1234" }]);
        host.Pump(a, b);
        Assert.Equal("New Arrivals", Assert.Single(b.Colonies).Name);

        // A leaves; the colony stays on everyone's globe.
        a.Disconnect();
        host.Pump(a, b);
        Assert.Single(b.Colonies);
    }

    [Fact]
    public void ColoniesFromTheLobbyAreIgnored()
    {
        var host = new Host(new WorldState { Definition = Planet(), Tick = 1000 });
        var a = host.Join("A", 1);
        host.Pump(a);

        a.SendColonies([new ColonyInfo { Name = "Ghost", Tile = "1" }]);
        host.Pump(a);
        Assert.Empty(a.Colonies);
    }

    [Fact]
    public void LeavingTheWorldStopsHoldingTimeBack()
    {
        var host = new Host(new WorldState { Definition = Planet(), Tick = 1000 });
        var a = host.Join("A", 1);
        var b = host.Join("B", 2);
        host.Pump(a, b);
        a.EnterWorld("world-1");
        b.EnterWorld("world-1");
        a.VoteSpeed(GameSpeed.Fast);
        host.Pump(a, b);
        a.ReportAuthority(3000, 1000);
        b.ReportAuthority(1000, 1000);
        host.Pump(a, b);
        Assert.Equal(1000 + 270, a.LastGrant?.HorizonTick); // speed 2: 180 tps * 1.5 s of slack

        b.LeaveWorld();
        host.Pump(a, b);
        Assert.Equal(3000 + 270, a.LastGrant?.HorizonTick);
        Assert.False(b.Players.Single(p => p.Name == "B").InWorld);
    }

    [Fact]
    public void WorldStateRoundTripsThroughItsFileFormat()
    {
        var state = new WorldState
        {
            Definition = Planet(),
            Tick = 3_600_000,
            Colonies = [new ColonyInfo { OwnerSteamId = 76561198000000001, OwnerName = "Vanket", Name = "Дом", Tile = "1234" }],
            ModListHash = "abc",
            GameVersion = "1.6.4633 rev1261",
        };

        var copy = WorldState.Deserialize(state.Serialize());

        Assert.Equal(state.Tick, copy.Tick);
        Assert.Equal(state.Definition.SeedString, copy.Definition!.SeedString);
        Assert.Equal(state.Definition.PlanetCoverage, copy.Definition.PlanetCoverage);
        Assert.Equal(state.Definition.Factions, copy.Definition.Factions);
        Assert.Equal("Дом", Assert.Single(copy.Colonies).Name);
        Assert.Equal("abc", copy.ModListHash);
        Assert.Equal("1.6.4633 rev1261", copy.GameVersion);
    }

    [Fact]
    public void RestoredWorldEnforcesItsModList()
    {
        var world = new WorldState { Definition = Planet(), ModListHash = "not-what-anyone-has", GameVersion = "1.6" };
        var host = new Host(world);
        var a = host.Join("A", 1);
        host.Pump(a);

        Assert.Equal(KickReason.ModListMismatch, a.KickReason);
    }

    [Fact]
    public void CreatedWorldRemembersModsAndVersion()
    {
        var host = new Host();
        var a = host.Join("A", 1);
        host.Pump(a);
        a.CreateWorld(Planet(), 0);
        host.Pump(a);

        Assert.Equal("1.6", host.Server.World.GameVersion);
        Assert.Equal(ModListHash.Compute([new ModEntry("ludeon.rimworld", "Core", "1.6", 0)]), host.Server.World.ModListHash);
    }

    [Fact]
    public void WithHostCreatesWorldOnlyTheHostPicksThePlanet()
    {
        var host = new Host(settings: new ServerSettings { HostCreatesWorld = true });
        var me = host.Join("Host", 1);
        host.Pump(me);
        var friend = host.Join("Friend", 2);
        host.Pump(me, friend);
        Assert.True(friend.HostCreatesWorld);

        var answered = false;
        friend.WorldChanged += () => answered = true;
        friend.CreateWorld(Planet("friends-planet"), 5);
        host.Pump(me, friend);
        Assert.Null(host.Server.World.Definition);
        Assert.True(answered); // the refusal is answered, so the client stops waiting

        me.CreateWorld(Planet("hosts-planet"), 7);
        host.Pump(me, friend);
        Assert.Equal("hosts-planet", friend.World?.WorldId);
    }

    [Fact]
    public void RepeatedCreateWithTheSameIdKeepsTheWorld()
    {
        var host = new Host();
        var a = host.Join("A", 1);
        host.Pump(a);
        a.CreateWorld(Planet("same"), 100);
        host.Pump(a);
        var changesAfterFirst = host.WorldChanges;
        a.CreateWorld(Planet("same"), 200);
        host.Pump(a);

        Assert.Equal("same", a.World?.WorldId);
        Assert.Equal(100, host.Server.World.Tick);
        Assert.Equal(changesAfterFirst, host.WorldChanges);
        Assert.Single(host.Server.World.Chronicle, e => e.Kind == Shared.World.ChronicleKind.WorldCreated);
    }
}
