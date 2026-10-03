using RimMult.ClientCore;
using RimMult.ServerCore;
using RimMult.Shared.Mods;
using RimMult.Shared.Net;
using RimMult.Shared.Packets;
using RimMult.Shared.Serialization;
using RimMult.Shared.World;

namespace RimMult.Tests;

public class ParcelTests
{
    private sealed class Host
    {
        public readonly TransportHub Hub = new();
        public readonly GameServer Server;
        public readonly LoopbackEndpoint Loopback;
        public int WorldChanges;
        private double _time;

        public Host(WorldState world)
        {
            Server = new GameServer(new ServerSettings(), Hub, world: world);
            Server.WorldChanged += () => WorldChanges++;
            Hub.Attach(Server);
            Loopback = new LoopbackEndpoint(Hub);
            Hub.AddEndpoint(Loopback);
        }

        public ClientSession Join(string name, ulong steamId, List<MailItem> inbox)
        {
            var session = new ClientSession(Loopback.CreateClient(), new ClientHello
            {
                SteamId = steamId,
                DisplayName = name,
                GameVersion = "1.6",
                Mods = [new ModEntry("ludeon.rimworld", "Core", "1.6", 0)],
            });
            session.ParcelReceived += inbox.Add;
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

    [Fact]
    public void ParcelReachesOnlinePlayerAndIsForgottenAfterAck()
    {
        var host = new Host(World());
        List<MailItem> inboxA = [], inboxB = [];
        var a = host.Join("A", 1, inboxA);
        var b = host.Join("B", 2, inboxB);
        host.Pump(a, b);
        a.EnterWorld("w");
        b.EnterWorld("w");
        host.Pump(a, b);

        a.SendParcel(2, "20", "Steel x200", [1, 2, 3]);
        host.Pump(a, b);

        var parcel = Assert.Single(inboxB);
        Assert.Equal("A", parcel.FromName);
        Assert.Equal("Steel x200", parcel.Summary);
        Assert.Equal([1, 2, 3], parcel.Payload);
        Assert.Empty(inboxA);
        Assert.Single(host.Server.World.Mail);

        b.AckParcel(parcel.Id);
        host.Pump(a, b);
        Assert.Empty(host.Server.World.Mail);
    }

    [Fact]
    public void ParcelWaitsForOfflinePlayerAndIsResentUntilAcked()
    {
        var host = new Host(World());
        List<MailItem> inboxA = [], inboxB = [];
        var a = host.Join("A", 1, inboxA);
        host.Pump(a);
        a.EnterWorld("w");
        host.Pump(a);

        a.SendParcel(2, "20", "Medicine x10", [9]);
        host.Pump(a);
        Assert.Single(host.Server.World.Mail);

        // B comes online later: in the lobby nothing arrives, entering the world delivers it.
        var b = host.Join("B", 2, inboxB);
        host.Pump(a, b);
        Assert.Empty(inboxB);
        b.EnterWorld("w");
        host.Pump(a, b);
        Assert.Single(inboxB);

        // Not acknowledged (e.g. disconnect before landing): delivered again next time.
        b.LeaveWorld();
        host.Pump(a, b);
        b.EnterWorld("w");
        host.Pump(a, b);
        Assert.Equal(2, inboxB.Count);
        Assert.Equal(inboxB[0].Id, inboxB[1].Id);
    }

    [Fact]
    public void ParcelToAPlayerWithoutColonyComesBack()
    {
        var host = new Host(World());
        List<MailItem> inboxA = [];
        var a = host.Join("A", 1, inboxA);
        host.Pump(a);
        a.EnterWorld("w");
        host.Pump(a);

        a.SendParcel(99, "30", "Gold x5", [7]);
        host.Pump(a);

        var back = Assert.Single(inboxA);
        Assert.True(back.Returned);
        Assert.Equal("Gold x5", back.Summary);
    }

    [Fact]
    public void ParcelsFromTheLobbyAreIgnored()
    {
        var host = new Host(World());
        List<MailItem> inbox = [];
        var a = host.Join("A", 1, inbox);
        host.Pump(a);

        a.SendParcel(2, "20", "x", [1]);
        host.Pump(a);
        Assert.Empty(host.Server.World.Mail);
    }

    [Fact]
    public void AckOnlyRemovesYourOwnParcels()
    {
        var host = new Host(World());
        List<MailItem> inboxA = [], inboxB = [];
        var a = host.Join("A", 1, inboxA);
        var b = host.Join("B", 2, inboxB);
        host.Pump(a, b);
        a.EnterWorld("w");
        b.EnterWorld("w");
        host.Pump(a, b);
        a.SendParcel(2, "20", "x", [1]);
        host.Pump(a, b);

        a.AckParcel(inboxB.Single().Id);
        host.Pump(a, b);
        Assert.Single(host.Server.World.Mail);
    }

    [Fact]
    public void MailSurvivesTheWorldFile()
    {
        var world = World();
        world.NextMailId = 5;
        world.Mail.Add(new MailItem { Id = 4, FromOwner = 1, FromName = "A", ToOwner = 2, ToTile = "20", Summary = "Сталь x200", Payload = [1, 2] });

        var copy = WorldState.Deserialize(world.Serialize());

        Assert.Equal(5, copy.NextMailId);
        var item = Assert.Single(copy.Mail);
        Assert.Equal("Сталь x200", item.Summary);
        Assert.Equal([1, 2], item.Payload);
    }

    [Fact]
    public void Version1WorldFilesStillLoad()
    {
        // Version 1 = the stage 2 layout, without the mail section.
        var writer = new ByteWriter();
        writer.WriteByte(1);
        writer.WriteBool(false);
        writer.WriteVarInt(777);
        ColonyInfo.WriteList(writer, []);
        writer.WriteString("hash");
        writer.WriteString("1.6");

        var state = WorldState.Deserialize(writer.ToArray());
        Assert.Equal(777, state.Tick);
        Assert.Empty(state.Mail);
        Assert.Equal(1, state.NextMailId);
    }

    [Fact]
    public void LargeReliableMessagesAreSplitAndReassembled()
    {
        var payload = new byte[1_300_000];
        new Random(1).NextBytes(payload);

        var frames = P2PFrame.ReliableFrames(payload);
        Assert.Equal(3, frames.Count);
        Assert.All(frames, f => Assert.True(f.Length <= P2PFrame.MaxReliableChunk + 2));

        var reassembler = new P2PReassembler();
        byte[]? message = null;
        foreach (var frame in frames)
        {
            Assert.True(P2PFrame.TryParse(frame, frame.Length, out var kind, out var last, out var part));
            Assert.Equal(P2PFrameKind.ReliablePart, kind);
            Assert.Null(message);
            message = reassembler.Add(part, last == 1);
        }
        Assert.Equal(payload, message);
    }

    [Fact]
    public void SmallReliableMessagesStayOneFrame()
    {
        var frames = P2PFrame.ReliableFrames([1, 2, 3]);
        var frame = Assert.Single(frames);
        Assert.True(P2PFrame.TryParse(frame, frame.Length, out var kind, out _, out _));
        Assert.Equal(P2PFrameKind.Reliable, kind);
    }
}
