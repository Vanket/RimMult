using RimMult.ClientCore;
using RimMult.ServerCore;
using RimMult.Shared.Mods;
using RimMult.Shared.Packets;
using RimMult.Shared.World;

namespace RimMult.Tests;

/// <summary>The world's planet: the creator's, kept by the server, fetched by every game whose planet differs.</summary>
public class WorldTerrainTests
{
    private sealed class Host
    {
        public readonly TransportHub Hub = new();
        public readonly GameServer Server;
        public readonly LoopbackEndpoint Loopback;
        private double _time;

        public Host(WorldState? world = null, bool hostCreatesWorld = true)
        {
            Server = new GameServer(new ServerSettings { HostCreatesWorld = hostCreatesWorld }, Hub, world: world);
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

    private static WorldState World() => new() { Definition = new WorldDefinition { WorldId = "w", SeedString = "шахматы" }, Tick = 100 };

    private static readonly byte[] Planet = Enumerable.Range(0, 300_000).Select(i => (byte)(i * 7)).ToArray();

    [Fact]
    public void PacketsRoundTrip()
    {
        var terrain = Assert.IsType<WorldTerrain>(PacketCodec.Decode(PacketCodec.Encode(new WorldTerrain { Hash = "ab", Data = Planet })));
        Assert.Equal("ab", terrain.Hash);
        Assert.Equal(Planet, terrain.Data);
        Assert.IsType<TerrainRequest>(PacketCodec.Decode(PacketCodec.Encode(new TerrainRequest())));

        var clock = Assert.IsType<WorldClock>(PacketCodec.Decode(PacketCodec.Encode(new WorldClock { Tick = 5, TerrainHash = "cd", WantsTerrain = true })));
        Assert.Equal("cd", clock.TerrainHash);
        Assert.True(clock.WantsTerrain);
    }

    [Fact]
    public void TheHashIsTheSameForTheSameBytesOnly()
    {
        Assert.Equal(WorldTerrain.HashOf(Planet), WorldTerrain.HashOf(Planet.ToArray()));
        var other = Planet.ToArray();
        other[^1] ^= 1;
        Assert.NotEqual(WorldTerrain.HashOf(Planet), WorldTerrain.HashOf(other));
        Assert.Equal(32, WorldTerrain.HashOf(Planet).Length);
    }

    [Fact]
    public void TheHostGivesThePlanetAndOthersFetchIt()
    {
        var host = new Host(World());
        var me = host.Join("Host", 1);
        var friend = host.Join("Friend", 2);
        host.Pump(me, friend);
        var wanted = 0;
        me.TerrainWanted += () => wanted++;
        var friendWanted = 0;
        friend.TerrainWanted += () => friendWanted++;
        string? announced = null;
        friend.TerrainAnnounced += h => announced = h;
        (string Hash, byte[] Data)? received = null;
        friend.TerrainReceived += (h, d) => received = (h, d);

        // The friend is in first: the server doesn't want the planet from them (not theirs to give).
        friend.EnterWorld("w");
        host.Pump(me, friend);
        Assert.Equal(0, friendWanted);

        // The host enters a world without a planet: it is asked for one and sends its own.
        me.EnterWorld("w");
        host.Pump(me, friend);
        Assert.Equal(1, wanted);
        me.SendTerrain(Planet);
        host.Pump(me, friend);
        Assert.Equal(WorldTerrain.HashOf(Planet), host.Server.World.TerrainHash);

        // The friend already in the world hears of it (without the data) and fetches it.
        Assert.Equal(WorldTerrain.HashOf(Planet), announced);
        Assert.Null(received);
        friend.RequestTerrain();
        host.Pump(me, friend);
        Assert.Equal(WorldTerrain.HashOf(Planet), received!.Value.Hash);
        Assert.Equal(Planet, received.Value.Data);
    }

    [Fact]
    public void EnteringLaterTellsTheHashAndAskingWorksBeforeEntering()
    {
        var world = World();
        world.TerrainData = Planet;
        world.TerrainHash = WorldTerrain.HashOf(Planet);
        var host = new Host(world);
        var me = host.Join("Host", 1);
        var friend = host.Join("Friend", 2);
        host.Pump(me, friend);
        var wanted = 0;
        me.TerrainWanted += () => wanted++;
        string? announced = null;
        me.TerrainAnnounced += h => announced = h;
        byte[]? atFriend = null;
        friend.TerrainReceived += (_, d) => atFriend = d;

        // A new colony asks before its site is picked, before entering the world.
        friend.RequestTerrain();
        host.Pump(me, friend);
        Assert.Equal(Planet, atFriend);

        // Entering: the clock carries the hash, nobody is asked for a planet the server already has.
        me.EnterWorld("w");
        host.Pump(me, friend);
        Assert.Equal(0, wanted);
        Assert.Equal(world.TerrainHash, announced);
    }

    [Fact]
    public void OnlyTheHostsPlanetCountsAndOnlyTheFirst()
    {
        var host = new Host(World());
        var me = host.Join("Host", 1);
        var friend = host.Join("Friend", 2);
        host.Pump(me, friend);
        me.EnterWorld("w");
        friend.EnterWorld("w");
        host.Pump(me, friend);

        friend.SendTerrain([1, 2, 3]);
        host.Pump(me, friend);
        Assert.Empty(host.Server.World.TerrainData);

        me.SendTerrain(Planet);
        me.SendTerrain([4, 5, 6]);
        host.Pump(me, friend);
        Assert.Equal(Planet, host.Server.World.TerrainData);
    }

    [Fact]
    public void NoPlanetIsSentWithoutOneOnTheServer()
    {
        var host = new Host(World());
        var me = host.Join("Host", 1);
        host.Pump(me);
        var received = 0;
        me.TerrainReceived += (_, _) => received++;
        me.RequestTerrain();
        host.Pump(me);
        Assert.Equal(0, received);
    }

    [Fact]
    public void OnADedicatedServerTheWorldsCreatorGivesIt()
    {
        var host = new Host(hostCreatesWorld: false);
        var first = host.Join("A", 1);
        var second = host.Join("B", 2);
        host.Pump(first, second);
        second.CreateWorld(new WorldDefinition { WorldId = "w", SeedString = "s" }, 5);
        host.Pump(first, second);
        var firstWanted = 0;
        first.TerrainWanted += () => firstWanted++;
        var secondWanted = 0;
        second.TerrainWanted += () => secondWanted++;
        first.EnterWorld("w");
        second.EnterWorld("w");
        host.Pump(first, second);
        Assert.Equal((0, 1), (firstWanted, secondWanted));

        first.SendTerrain([1]);
        host.Pump(first, second);
        Assert.Empty(host.Server.World.TerrainData);

        second.SendTerrain(Planet);
        host.Pump(first, second);
        Assert.Equal(Planet, host.Server.World.TerrainData);
    }

    [Fact]
    public void ThePlanetSurvivesTheWorldFileButNotTheHostsSave()
    {
        var world = World();
        world.TerrainData = Planet;
        world.TerrainHash = WorldTerrain.HashOf(Planet);

        var copy = WorldState.Deserialize(world.Serialize());
        Assert.Equal(Planet, copy.TerrainData);
        Assert.Equal(world.TerrainHash, copy.TerrainHash);

        // An in-game host's save leaves it out: its game is the planet and sends it again.
        var saved = WorldState.Deserialize(world.Serialize(withTerrain: false));
        Assert.Empty(saved.TerrainData);
        Assert.Equal("", saved.TerrainHash);
        Assert.Equal("шахматы", saved.Definition!.SeedString);
    }
}
