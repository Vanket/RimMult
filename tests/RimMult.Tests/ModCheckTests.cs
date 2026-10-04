using RimMult.ClientCore;
using RimMult.ServerCore;
using RimMult.Shared;
using RimMult.Shared.Mods;
using RimMult.Shared.Packets;

namespace RimMult.Tests;

/// <summary>Asking a server for its mods before joining (to make ours the same first).</summary>
public class ModCheckTests
{
    private sealed class Host
    {
        public readonly TransportHub Hub = new();
        public readonly GameServer Server;
        public readonly LoopbackEndpoint Loopback;
        private double _time;

        public Host()
        {
            Server = new GameServer(new ServerSettings(), Hub);
            Hub.Attach(Server);
            Loopback = new LoopbackEndpoint(Hub);
            Hub.AddEndpoint(Loopback);
        }

        public ClientSession Session(string name, List<ModEntry> mods, bool checkOnly = false)
        {
            var session = new ClientSession(Loopback.CreateClient(), new ClientHello { SteamId = (ulong)name.Length, DisplayName = name, GameVersion = "1.6", Mods = mods }, checkOnly);
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

    private static List<ModEntry> Mods(params string[] ids) =>
        [new ModEntry("ludeon.rimworld", "Core", "1.6", 0), .. ids.Select(id => new ModEntry(id, id, "v1", 100))];

    [Fact]
    public void PacketsRoundTrip()
    {
        var query = Assert.IsType<ModListQuery>(PacketCodec.Decode(PacketCodec.Encode(new ModListQuery())));
        Assert.Equal(ProtocolInfo.Version, query.ProtocolVersion);

        var answer = Assert.IsType<ServerModList>(PacketCodec.Decode(PacketCodec.Encode(new ServerModList { GameVersion = "1.6", Mods = Mods("a") })));
        Assert.Equal(("1.6", 2), (answer.GameVersion, answer.Mods!.Count));
        Assert.Null(Assert.IsType<ServerModList>(PacketCodec.Decode(PacketCodec.Encode(new ServerModList()))).Mods);
    }

    [Fact]
    public void CheckShowsTheHostsModsWithoutJoining()
    {
        var host = new Host();
        var me = host.Session("Host", Mods("a", "b"));
        host.Pump(me);

        var friend = host.Session("Friend", Mods("b", "a", "extra"), checkOnly: true);
        host.Pump(me, friend);

        Assert.True(friend.IsModCheck);
        Assert.Equal(ClientState.Disconnected, friend.State);
        Assert.Null(friend.KickReason);
        Assert.Equal(ProtocolInfo.Version, friend.ServerProtocol);
        Assert.Equal(["ludeon.rimworld", "a", "b"], friend.ServerMods!.Select(m => m.PackageId));
        Assert.Equal("extra", Assert.Single(friend.ModDiff!.Extra).PackageId);
        // Nobody joined: the host still plays alone and saw no one come and go.
        Assert.Equal(1, host.Server.PlayerCount);
        Assert.Single(me.Players);
    }

    [Fact]
    public void MatchingModsGiveAnEmptyDiff()
    {
        var host = new Host();
        var me = host.Session("Host", Mods("a"));
        host.Pump(me);

        var friend = host.Session("Friend", Mods("a"), checkOnly: true);
        host.Pump(me, friend);

        Assert.True(friend.ModDiff!.IsEmpty);
    }

    [Fact]
    public void BeforeAnyoneJoinedThereIsNoListYet()
    {
        var host = new Host();
        var friend = host.Session("Friend", Mods("a"), checkOnly: true);
        host.Pump(friend);

        Assert.Equal(ClientState.Disconnected, friend.State);
        Assert.Null(friend.ServerMods);
        Assert.Equal(ProtocolInfo.Version, friend.ServerProtocol);
    }

    [Fact]
    public void AnOlderRimMultIsToldTheHostsModsToUpdate()
    {
        var host = new Host();
        var me = host.Session("Host", Mods("a"));
        host.Pump(me);

        var old = new ClientSession(host.Loopback.CreateClient(), new ClientHello
        {
            ProtocolVersion = ProtocolInfo.Version - 1, SteamId = 2, DisplayName = "Friend", GameVersion = "1.6", Mods = Mods("a"),
        });
        old.Start();
        host.Pump(me, old);

        Assert.Equal(KickReason.ProtocolMismatch, old.KickReason);
        Assert.Equal(["ludeon.rimworld", "a"], old.ServerMods!.Select(m => m.PackageId));
    }
}
