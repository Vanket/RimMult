using RimMult.ClientCore;
using RimMult.ServerCore;
using RimMult.Shared.Mods;
using RimMult.Shared.Packets;
using RimMult.Shared.Time;
using RimMult.Shared.World;

namespace RimMult.Tests;

public class AdminPanelTests
{
    private sealed class Host
    {
        public readonly TransportHub Hub = new();
        public readonly GameServer Server;
        public readonly LoopbackEndpoint Loopback;
        private double _time;

        public Host(WorldState world)
        {
            Server = new GameServer(new ServerSettings { AllowPvp = true }, Hub, world: world);
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
            new ColonyInfo { OwnerSteamId = 76561198000000001, OwnerName = "Host", Name = "A", Tile = "10" },
            new ColonyInfo { OwnerSteamId = 76561198000000002, OwnerName = "Bob", Name = "B", Tile = "20" },
            new ColonyInfo { OwnerSteamId = 76561198000000003, OwnerName = "Away", Name = "C", Tile = "30" },
        ],
    };

    private const ulong HostKey = 76561198000000001;
    private const ulong BobKey = 76561198000000002;
    private const ulong AwayKey = 76561198000000003;

    private static (Host Host, WorldState World, ClientSession Admin, ClientSession Bob) Two()
    {
        var world = World();
        var host = new Host(world);
        var admin = host.Join("Host", HostKey);
        host.Pump(admin);
        var bob = host.Join("Bob", BobKey);
        host.Pump(admin, bob);
        admin.EnterWorld("w");
        bob.EnterWorld("w");
        host.Pump(admin, bob);
        return (host, world, admin, bob);
    }

    [Fact]
    public void OnlyAdminsMayAndTheListSaysWhoIs()
    {
        var (host, world, admin, bob) = Two();
        Assert.True(admin.IsAdmin);
        Assert.False(bob.IsAdmin);

        bob.SendAdmin(new AdminAction { Kind = AdminActionKind.GiveItems, Target = BobKey, Summary = "Gold x1000", Payload = [1] });
        bob.SendAdmin(new AdminAction { Kind = AdminActionKind.SetRelation, Target = HostKey, Other = BobKey, Relation = PlayerRelation.Hostile });
        host.Pump(admin, bob);
        Assert.Empty(world.Mail);
        Assert.Empty(world.Relations);

        // Named an admin: the panel is theirs too.
        admin.SendChat("/admin Bob");
        host.Pump(admin, bob);
        Assert.True(bob.IsAdmin);
    }

    [Fact]
    public void GiftsReachEvenThoseAwayAndStayOutOfTheChronicle()
    {
        var (host, world, admin, bob) = Two();
        var inbox = new List<MailItem>();
        bob.ParcelReceived += inbox.Add;
        var chronicle = world.Chronicle.Count;

        admin.SendAdmin(new AdminAction { Kind = AdminActionKind.GiveItems, Target = BobKey, Summary = "Steel x500", Payload = [5] });
        admin.SendAdmin(new AdminAction { Kind = AdminActionKind.GiveResearch, Target = AwayKey, Summary = "2 projects", Payload = "Electricity\nBatteries"u8.ToArray() });
        host.Pump(admin, bob);

        var gift = Assert.Single(inbox);
        Assert.True(ParcelAddress.IsAdminGift(gift.ToTile));
        Assert.Equal("Host", gift.FromName);
        Assert.Equal(new byte[] { 5 }, gift.Payload);
        // The player who is away gets theirs when they come.
        var research = Assert.Single(world.Mail, m => m.ToOwner == AwayKey);
        Assert.True(ParcelAddress.IsAdminResearch(research.ToTile));
        Assert.Equal(chronicle, world.Chronicle.Count);
        // Unknown players get nothing.
        admin.SendAdmin(new AdminAction { Kind = AdminActionKind.GiveItems, Target = 12345, Payload = [1] });
        host.Pump(admin, bob);
        Assert.DoesNotContain(world.Mail, m => m.ToOwner == 12345);
    }

    [Fact]
    public void PlayersCantFakeAnAdminParcel()
    {
        var (host, world, admin, bob) = Two();
        bob.SendParcel(BobKey, ParcelAddress.ForAdminGift(), "Gold x9999", [1]);
        host.Pump(admin, bob);
        Assert.Empty(world.Mail);
    }

    [Fact]
    public void RelationsMarketAndPause()
    {
        var (host, world, admin, bob) = Two();
        admin.SendAdmin(new AdminAction { Kind = AdminActionKind.SetRelation, Target = BobKey, Other = AwayKey, Relation = PlayerRelation.Allied });
        host.Pump(admin, bob);
        Assert.Equal(PlayerRelation.Allied, bob.RelationWith(AwayKey));

        bob.SendMarket(new MarketAction { Kind = MarketActionKind.PostLot, Price = 100, Summary = "Gold x5", Payload = [5] });
        host.Pump(admin, bob);
        var inbox = new List<MailItem>();
        bob.ParcelReceived += inbox.Add;
        admin.SendAdmin(new AdminAction { Kind = AdminActionKind.RemoveLot, Id = world.MarketLots.Single().Id });
        world.Prices.Bought("Steel", 3000);
        admin.SendAdmin(new AdminAction { Kind = AdminActionKind.ResetPrices });
        host.Pump(admin, bob);
        Assert.Empty(bob.MarketLots);
        Assert.Equal(ParcelAddress.MarketKind.LotReturned, ParcelAddress.TryParseMarket(Assert.Single(inbox).ToTile, out var kind) ? kind : default);
        Assert.Empty(world.Prices.Items);

        admin.VoteSpeed(GameSpeed.Normal);
        bob.VoteSpeed(GameSpeed.Normal);
        admin.SendAdmin(new AdminAction { Kind = AdminActionKind.PauseWorld });
        host.Pump(admin, bob);
        Assert.Equal(GameSpeed.Paused, bob.LastGrant!.Speed);
        admin.SendAdmin(new AdminAction { Kind = AdminActionKind.ResumeWorld });
        host.Pump(admin, bob);
        Assert.Equal(GameSpeed.Normal, bob.LastGrant!.Speed);
    }
}
