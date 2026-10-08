using RimMult.ClientCore;
using RimMult.ServerCore;
using RimMult.Shared.Mods;
using RimMult.Shared.Packets;
using RimMult.Shared.World;

namespace RimMult.Tests;

/// <summary>The world's NPC settlements: the creator's layout, kept by the server, given to everyone entering the world.</summary>
public class NpcLayoutTests
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

    private static List<NpcSettlement> Layout(params string[] tiles) =>
        tiles.Select((t, i) => new NpcSettlement { Tile = t, Def = "Settlement", FactionDef = "OutlanderCivil", FactionIndex = i % 2, Name = "Town " + t }).ToList();

    [Fact]
    public void PacketsRoundTrip()
    {
        var layout = Assert.IsType<NpcLayout>(PacketCodec.Decode(PacketCodec.Encode(new NpcLayout { Settlements = Layout("12", "345") })));
        var second = layout.Settlements[1];
        Assert.Equal(("345", "Settlement", "OutlanderCivil", 1, "Town 345"), (second.Tile, second.Def, second.FactionDef, second.FactionIndex, second.Name));

        var clock = Assert.IsType<WorldClock>(PacketCodec.Decode(PacketCodec.Encode(new WorldClock { Tick = 77, HasNpcLayout = true })));
        Assert.Equal((77L, true), (clock.Tick, clock.HasNpcLayout));
    }

    [Fact]
    public void TheHostGivesTheLayoutAndEveryoneEnteringGetsIt()
    {
        var host = new Host(World());
        var me = host.Join("Host", 1);
        var friend = host.Join("Friend", 2);
        host.Pump(me, friend);
        var wanted = 0;
        me.NpcLayoutWanted += () => wanted++;
        List<NpcSettlement>? atFriend = null;
        friend.NpcLayoutReceived += l => atFriend = l;

        // The host enters a world without a layout: it is asked for one and sends its own.
        me.EnterWorld("w");
        host.Pump(me, friend);
        Assert.Equal(1, wanted);
        me.SendNpcLayout(Layout("10", "20", "30"));
        host.Pump(me, friend);
        Assert.Equal(3, host.Server.World.NpcSettlements.Count);

        // The friend entering afterwards gets it with the clock.
        friend.EnterWorld("w");
        host.Pump(me, friend);
        Assert.Equal(["10", "20", "30"], atFriend!.Select(s => s.Tile));
    }

    [Fact]
    public void PlayersAlreadyInTheWorldGetTheLayoutWhenItArrives()
    {
        var host = new Host(World());
        var me = host.Join("Host", 1);
        var friend = host.Join("Friend", 2);
        host.Pump(me, friend);
        friend.EnterWorld("w");
        me.EnterWorld("w");
        host.Pump(me, friend);
        List<NpcSettlement>? atFriend = null;
        friend.NpcLayoutReceived += l => atFriend = l;

        me.SendNpcLayout(Layout("10"));
        host.Pump(me, friend);

        Assert.Equal("10", Assert.Single(atFriend!).Tile);
    }

    [Fact]
    public void OnlyTheHostsLayoutCountsAndOnlyTheFirst()
    {
        var host = new Host(World());
        var me = host.Join("Host", 1);
        var friend = host.Join("Friend", 2);
        host.Pump(me, friend);
        me.EnterWorld("w");
        friend.EnterWorld("w");
        host.Pump(me, friend);

        friend.SendNpcLayout(Layout("99"));
        host.Pump(me, friend);
        Assert.Empty(host.Server.World.NpcSettlements);

        me.SendNpcLayout(Layout("10"));
        me.SendNpcLayout(Layout("20", "30"));
        host.Pump(me, friend);
        Assert.Equal("10", Assert.Single(host.Server.World.NpcSettlements).Tile);
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
        first.EnterWorld("w");
        second.EnterWorld("w");
        host.Pump(first, second);
        Assert.Equal(2UL, host.Server.World.CreatorOwner);

        first.SendNpcLayout(Layout("1"));
        host.Pump(first, second);
        Assert.Empty(host.Server.World.NpcSettlements);

        second.SendNpcLayout(Layout("2"));
        host.Pump(first, second);
        Assert.Equal("2", Assert.Single(host.Server.World.NpcSettlements).Tile);
    }

    [Fact]
    public void LayoutAndCreatorSurviveTheWorldFile()
    {
        var world = World();
        world.NpcSettlements = Layout("10", "20");
        world.CreatorOwner = 76561198000000001;

        var copy = WorldState.Deserialize(world.Serialize());

        Assert.Equal(["10", "20"], copy.NpcSettlements.Select(s => s.Tile));
        Assert.Equal(76561198000000001UL, copy.CreatorOwner);

        // Format 5: without them (empty list: one zero, creator: 8 bytes) and formats 7-10 (treaties, market, prices, planet: eleven bytes).
        var bytes = World().Serialize();
        var old = bytes.Take(bytes.Length - 1 - 8 - 2 - 3 - 4 - 2).ToArray();
        old[0] = 5;
        var five = WorldState.Deserialize(old);
        Assert.Empty(five.NpcSettlements);
        Assert.Equal(0UL, five.CreatorOwner);
    }
}
