using RimMult.ClientCore;
using RimMult.Net.LiteNet;
using RimMult.ServerCore;
using RimMult.Shared.Mods;
using RimMult.Shared.Packets;
using RimMult.Shared.Time;

namespace RimMult.Tests;

/// <summary>Client and server together, the way the game uses them: over loopback (host) and over real UDP.</summary>
public class ClientSessionTests
{
    private sealed class Host
    {
        public readonly TransportHub Hub = new();
        public readonly GameServer Server;
        public readonly LoopbackEndpoint Loopback;
        private double _time;

        public Host(ServerSettings? settings = null)
        {
            Server = new GameServer(settings ?? new ServerSettings(), Hub);
            Hub.Attach(Server);
            Loopback = new LoopbackEndpoint(Hub);
            Hub.AddEndpoint(Loopback);
        }

        public ClientSession Join(string name, List<ModEntry>? mods = null, string? password = null, ulong steamId = 0)
        {
            var session = new ClientSession(Loopback.CreateClient(), Hello(name, mods, password, steamId));
            session.Start();
            return session;
        }

        /// <summary>Runs server and clients until nothing changes anymore.</summary>
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
        [new ModEntry("ludeon.rimworld", "Core", "1.6", 0), .. ids.Select(id => new ModEntry(id, id.ToUpperInvariant(), "v1", 100))];

    private static ClientHello Hello(string name, List<ModEntry>? mods = null, string? password = null, ulong steamId = 0) => new()
    {
        SteamId = steamId,
        DisplayName = name,
        GameVersion = "1.6",
        Mods = mods ?? Mods(),
        Password = password,
    };

    [Fact]
    public void HostAndFriendSeeEachOtherAndChat()
    {
        var host = new Host();
        var me = host.Join("Vanket", steamId: 1);
        var friend = host.Join("Friend", steamId: 2);
        host.Pump(me, friend);

        Assert.Equal(ClientState.Connected, me.State);
        Assert.Equal(ClientState.Connected, friend.State);
        Assert.True(me.IsHost);
        Assert.False(friend.IsHost);
        Assert.Equal(["Vanket", "Friend"], friend.Players.Select(p => p.Name));

        friend.SendChat("привет");
        host.Pump(me, friend);

        var line = Assert.Single(me.Chat);
        Assert.Equal("Friend", line.SenderName);
        Assert.Equal("привет", line.Text);
        Assert.Single(friend.Chat);
    }

    [Fact]
    public void ClientsReceiveTheSharedClock()
    {
        var host = new Host();
        var me = host.Join("A");
        host.Pump(me);

        me.VoteSpeed(GameSpeed.Fast);
        host.Pump(me);

        Assert.Equal(GameSpeed.Fast, me.LastGrant?.Speed);
    }

    [Fact]
    public void ModMismatchShowsExactDifferences()
    {
        var host = new Host();
        var me = host.Join("Host", Mods("vef.core", "ceteam.combatextended"));
        host.Pump(me);

        var friend = host.Join("Friend", Mods("vef.core", "some.extra"));
        host.Pump(me, friend);

        Assert.Equal(ClientState.Disconnected, friend.State);
        Assert.Equal(KickReason.ModListMismatch, friend.KickReason);
        var diff = friend.ModDiff!;
        Assert.Equal("ceteam.combatextended", Assert.Single(diff.Missing).PackageId);
        Assert.Equal(100UL, diff.Missing[0].WorkshopId);
        Assert.Equal("some.extra", Assert.Single(diff.Extra).PackageId);
        Assert.Equal(1, host.Server.PlayerCount);
    }

    [Fact]
    public void WrongPasswordIsReportedToTheClient()
    {
        var host = new Host(new ServerSettings { Password = "secret" });
        var me = host.Join("Host", password: "secret");
        var friend = host.Join("Friend", password: "guess");
        host.Pump(me, friend);

        Assert.Equal(ClientState.Connected, me.State);
        Assert.Equal(KickReason.WrongPassword, friend.KickReason);
        Assert.Equal("Wrong password", friend.DisconnectReason);
    }

    [Fact]
    public void LeavingUpdatesTheRoster()
    {
        var host = new Host();
        var me = host.Join("Host");
        var friend = host.Join("Friend");
        host.Pump(me, friend);

        friend.Disconnect();
        host.Pump(me, friend);

        Assert.Equal(ClientState.Disconnected, friend.State);
        Assert.Equal("Host", Assert.Single(me.Players).Name);
    }

    [Fact]
    public void ShutdownDisconnectsEveryone()
    {
        var host = new Host();
        var me = host.Join("Host");
        var friend = host.Join("Friend");
        host.Pump(me, friend);

        host.Server.Shutdown();
        host.Pump(me, friend);

        Assert.Equal(ClientState.Disconnected, friend.State);
        Assert.Equal(KickReason.ServerShutdown, friend.KickReason);
    }

    [Fact]
    public void WorksOverRealUdp()
    {
        var hub = new TransportHub();
        var server = new GameServer(new ServerSettings { Password = "pw" }, hub);
        hub.Attach(server);

        // Try a few ports in case one is taken on the test machine.
        LiteNetServerEndpoint? udp = null;
        var port = 0;
        for (var candidate = 41000; candidate < 41020 && udp == null; candidate++)
        {
            var endpoint = new LiteNetServerEndpoint(hub, 10);
            if (endpoint.Start(candidate))
            {
                udp = endpoint;
                port = candidate;
            }
        }
        Assert.NotNull(udp);
        hub.AddEndpoint(udp);

        try
        {
            var good = new ClientSession(new LiteNetClientTransport("127.0.0.1", port), Hello("Good", password: "pw"));
            var bad = new ClientSession(new LiteNetClientTransport("127.0.0.1", port), Hello("Bad", password: "nope"));
            good.Start();
            bad.Start();

            var clock = System.Diagnostics.Stopwatch.StartNew();
            while (clock.Elapsed < TimeSpan.FromSeconds(10)
                   && (good.State != ClientState.Connected || good.Players.Count != 1 || bad.State != ClientState.Disconnected))
            {
                hub.Poll();
                server.Update(clock.Elapsed.TotalSeconds);
                good.Poll();
                bad.Poll();
                Thread.Sleep(5);
            }

            Assert.Equal(ClientState.Connected, good.State);
            Assert.Equal("Good", Assert.Single(good.Players).Name);
            // The kick reason travels inside the disconnect packet and must survive it.
            Assert.Equal(ClientState.Disconnected, bad.State);
            Assert.Equal(KickReason.WrongPassword, bad.KickReason);
            good.Disconnect();
        }
        finally
        {
            hub.Stop();
        }
    }
}
