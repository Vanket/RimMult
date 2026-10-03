using RimMult.ServerCore;
using RimMult.Shared;
using RimMult.Shared.Mods;
using RimMult.Shared.Net;
using RimMult.Shared.Packets;
using RimMult.Shared.Time;
using RimMult.Shared.World;

namespace RimMult.Tests;

public class GameServerTests
{
    private sealed class FakeTransport : IServerTransport
    {
        public GameServer Server = null!;
        public readonly List<(int Connection, IPacket Packet, DeliveryMode Mode)> Sent = new();
        public readonly HashSet<int> Disconnected = new();

        public void Send(int connectionId, byte[] data, DeliveryMode mode) =>
            Sent.Add((connectionId, PacketCodec.Decode(data), mode));

        public void Disconnect(int connectionId, byte[]? farewell = null)
        {
            if (farewell != null)
                Sent.Add((connectionId, PacketCodec.Decode(farewell), DeliveryMode.ReliableOrdered));
            Disconnected.Add(connectionId);
            Server.OnDisconnected(connectionId);
        }

        public List<T> To<T>(int connection) where T : IPacket =>
            Sent.Where(s => s.Connection == connection).Select(s => s.Packet).OfType<T>().ToList();
    }

    private static (GameServer Server, FakeTransport Transport) Create(ServerSettings? settings = null)
    {
        var transport = new FakeTransport();
        var server = new GameServer(settings ?? new ServerSettings(), transport);
        transport.Server = server;
        return (server, transport);
    }

    private static void Receive(GameServer server, int connection, IPacket packet)
    {
        var data = PacketCodec.Encode(packet);
        server.OnData(connection, data, 0, data.Length);
    }

    private static ClientHello Hello(ulong steamId, string name = "P", string mods = "mods", string? password = null) => new()
    {
        SteamId = steamId,
        DisplayName = name,
        GameVersion = "1.6",
        Mods = [new ModEntry("ludeon.rimworld", "Core", "1.6", 0), new ModEntry(mods, mods, "v1", 42)],
        Password = password,
    };

    [Fact]
    public void FirstPlayerBecomesHost()
    {
        var (server, transport) = Create();
        server.OnConnected(10);
        Receive(server, 10, Hello(1, "Vanket"));

        var welcome = Assert.Single(transport.To<ServerWelcome>(10));
        Assert.True(welcome.IsHost);
        var list = transport.To<PlayerList>(10).Last();
        Assert.Equal("Vanket", Assert.Single(list.Players).Name);
    }

    [Fact]
    public void ModListMismatchIsKicked()
    {
        var (server, transport) = Create();
        server.OnConnected(1);
        Receive(server, 1, Hello(1, mods: "A"));
        server.OnConnected(2);
        Receive(server, 2, Hello(2, mods: "B"));

        var kick = Assert.Single(transport.To<Kick>(2));
        Assert.Equal(KickReason.ModListMismatch, kick.Reason);
        Assert.Equal("A", kick.ServerMods![1].PackageId);
        Assert.Contains(2, transport.Disconnected);
        Assert.Equal(1, server.PlayerCount);
    }

    [Fact]
    public void WrongPasswordIsKicked()
    {
        var (server, transport) = Create(new ServerSettings { Password = "secret" });
        server.OnConnected(1);
        Receive(server, 1, Hello(1, password: "nope"));
        Assert.Equal(KickReason.WrongPassword, Assert.Single(transport.To<Kick>(1)).Reason);
    }

    [Fact]
    public void ProtocolMismatchIsKicked()
    {
        var (server, transport) = Create();
        server.OnConnected(1);
        var hello = Hello(1);
        hello.ProtocolVersion = ProtocolInfo.Version + 1;
        Receive(server, 1, hello);
        Assert.Equal(KickReason.ProtocolMismatch, Assert.Single(transport.To<Kick>(1)).Reason);
    }

    [Fact]
    public void ServerFullIsKicked()
    {
        var (server, transport) = Create(new ServerSettings { MaxPlayers = 1 });
        server.OnConnected(1);
        Receive(server, 1, Hello(1));
        server.OnConnected(2);
        Receive(server, 2, Hello(2));
        Assert.Equal(KickReason.ServerFull, Assert.Single(transport.To<Kick>(2)).Reason);
    }

    [Fact]
    public void SameSteamAccountCannotJoinTwice()
    {
        var (server, transport) = Create();
        server.OnConnected(1);
        Receive(server, 1, Hello(42));
        server.OnConnected(2);
        Receive(server, 2, Hello(42));
        Assert.Equal(KickReason.AlreadyConnected, Assert.Single(transport.To<Kick>(2)).Reason);
    }

    [Fact]
    public void PacketsBeforeHandshakeAreRejected()
    {
        var (server, transport) = Create();
        server.OnConnected(1);
        Receive(server, 1, new SpeedVote { Speed = GameSpeed.Fast });
        Assert.Equal(KickReason.BadData, Assert.Single(transport.To<Kick>(1)).Reason);
    }

    [Fact]
    public void GarbageIsRejected()
    {
        var (server, transport) = Create();
        server.OnConnected(1);
        server.OnData(1, [0xFF, 0xFF, 0x00], 0, 3);
        Assert.Equal(KickReason.BadData, Assert.Single(transport.To<Kick>(1)).Reason);
    }

    [Fact]
    public void HostLeavingPromotesNextPlayer()
    {
        var (server, transport) = Create(new ServerSettings { Time = new TimeSettings { VoteMode = SpeedVoteMode.Host } });
        server.OnConnected(1);
        Receive(server, 1, Hello(1, "A"));
        server.OnConnected(2);
        Receive(server, 2, Hello(2, "B"));

        server.OnDisconnected(1);

        var list = transport.To<PlayerList>(2).Last();
        Assert.True(Assert.Single(list.Players).IsHost);
        Receive(server, 2, new SpeedVote { Speed = GameSpeed.Fast });
        Assert.Equal(GameSpeed.Fast, server.Time.ResolveSpeed());
    }

    [Fact]
    public void ChatIsStampedWithSenderAndBroadcast()
    {
        var (server, transport) = Create();
        server.OnConnected(1);
        Receive(server, 1, Hello(1, "A"));
        server.OnConnected(2);
        Receive(server, 2, Hello(2, "B"));

        Receive(server, 2, new ChatMessage { SenderId = 999, Text = "  привет  " });

        foreach (var connection in new[] { 1, 2 })
        {
            var chat = Assert.Single(transport.To<ChatMessage>(connection));
            Assert.Equal(2, chat.SenderId);
            Assert.Equal("привет", chat.Text);
        }
    }

    [Fact]
    public void UpdateBroadcastsSharedClock()
    {
        var transport = new FakeTransport();
        var world = new WorldState { Definition = new WorldDefinition { WorldId = "w" }, Tick = 100 };
        var server = new GameServer(new ServerSettings(), transport, world: world);
        transport.Server = server;
        server.OnConnected(1);
        Receive(server, 1, Hello(1));
        Receive(server, 1, new EnterWorld { WorldId = "w" });
        Receive(server, 1, new SpeedVote { Speed = GameSpeed.Fast });
        Receive(server, 1, new AuthorityReport { Tick = 500, SustainableTicksPerSecond = 1000 });

        server.Update(0);
        server.Update(0.01); // within the grant interval: no second broadcast

        var grant = Assert.Single(transport.To<TickGrant>(1));
        Assert.Equal(GameSpeed.Fast, grant.Speed);
        Assert.Equal(500 + 270, grant.HorizonTick); // speed 2: 180 tps * 1.5 s of slack
        Assert.Equal(DeliveryMode.UnreliableSequenced, transport.Sent.Single(s => s.Packet is TickGrant).Mode);
    }

    [Fact]
    public void PinnedModHashWithoutKnownListKicksWithoutDiff()
    {
        var hello = Hello(1, mods: "A");
        var (server, transport) = Create(new ServerSettings { ModListHash = ModListHash.Compute(Hello(9, mods: "B").Mods) });
        server.OnConnected(1);
        Receive(server, 1, hello);

        var kick = Assert.Single(transport.To<Kick>(1));
        Assert.Equal(KickReason.ModListMismatch, kick.Reason);
        Assert.Null(kick.ServerMods);
    }

    [Fact]
    public void MatchingPinnedModHashIsAccepted()
    {
        var hello = Hello(1, mods: "A");
        var (server, transport) = Create(new ServerSettings { ModListHash = ModListHash.Compute(hello.Mods) });
        server.OnConnected(1);
        Receive(server, 1, hello);
        Assert.Single(transport.To<ServerWelcome>(1));
    }
}
