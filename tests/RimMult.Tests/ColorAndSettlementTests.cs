using RimMult.ClientCore;
using RimMult.ServerCore;
using RimMult.Shared.Mods;
using RimMult.Shared.Packets;
using RimMult.Shared.Serialization;
using RimMult.Shared.World;

namespace RimMult.Tests;

public class ColorAndSettlementTests
{
    private sealed class Host
    {
        public readonly TransportHub Hub = new();
        public readonly GameServer Server;
        public readonly LoopbackEndpoint Loopback;
        private double _time;

        public Host(WorldState world)
        {
            Server = new GameServer(new ServerSettings(), Hub, world: world);
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

    private static WorldState World() => new() { Definition = new WorldDefinition { WorldId = "w" }, Tick = 10 };

    [Fact]
    public void PlayersGetDistinctColorsThatStick()
    {
        var host = new Host(World());
        var a = host.Join("A", 100);
        host.Pump(a);
        var b = host.Join("B", 200);
        host.Pump(a, b);

        Assert.Equal(0, a.ColorOf(a.PlayerId)); // the first player is green
        Assert.Equal(1, b.ColorOf(b.PlayerId));

        // A leaves and comes back: same color; a newcomer gets the next free one.
        a.Disconnect();
        host.Pump(a, b);
        var c = host.Join("C", 300);
        host.Pump(b, c);
        var a2 = host.Join("A", 100);
        host.Pump(b, c, a2);

        Assert.Equal(2, c.ColorOf(c.PlayerId));
        Assert.Equal(0, a2.ColorOf(a2.PlayerId));
    }

    [Fact]
    public void ColoniesCarryTheirOwnersColor()
    {
        var host = new Host(World());
        var a = host.Join("A", 100);
        var b = host.Join("B", 200);
        host.Pump(a, b);
        b.EnterWorld("w");
        host.Pump(a, b);
        b.SendColonies([new ColonyInfo { Name = "B home", Tile = "5" }]);
        host.Pump(a, b);

        Assert.Equal(1, a.Colonies.Single().ColorIndex);
    }

    [Fact]
    public void DestroyedSettlementIsSharedWithEveryoneAndRemembered()
    {
        var world = World();
        var host = new Host(world);
        var a = host.Join("A", 100);
        var b = host.Join("B", 200);
        host.Pump(a, b);
        a.EnterWorld("w");
        host.Pump(a, b);

        a.ReportSettlementDestroyed("1234");
        a.ReportSettlementDestroyed("1234");
        host.Pump(a, b);

        Assert.Equal(["1234"], b.DestroyedSettlements);
        Assert.Equal(["1234"], WorldState.Deserialize(world.Serialize()).DestroyedSettlements);
    }

    [Fact]
    public void SettlementReportsFromTheLobbyAreIgnored()
    {
        var host = new Host(World());
        var a = host.Join("A", 100);
        host.Pump(a);
        a.ReportSettlementDestroyed("1");
        host.Pump(a);
        Assert.Empty(host.Server.World.DestroyedSettlements);
    }

    [Fact]
    public void Version2WorldFilesWithColoniesStillLoad()
    {
        // Format 2: colonies without the color byte, mail section, no colors/settlements section.
        var writer = new ByteWriter();
        writer.WriteByte(2);
        writer.WriteBool(false);
        writer.WriteVarInt(500);
        writer.WriteVarUInt(1);
        writer.WriteUInt64(7);
        writer.WriteString("Owner");
        writer.WriteString("Colony");
        writer.WriteString("42");
        writer.WriteString("hash");
        writer.WriteString("1.6");
        writer.WriteVarInt(3); // next mail id
        writer.WriteVarUInt(0); // no mail

        var state = WorldState.Deserialize(writer.ToArray());
        var colony = Assert.Single(state.Colonies);
        Assert.Equal("Colony", colony.Name);
        Assert.Equal("42", colony.Tile);
        Assert.Equal(3, state.NextMailId);
        Assert.Empty(state.PlayerColors);
    }
}
