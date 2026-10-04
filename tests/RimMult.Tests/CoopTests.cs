using RimMult.ClientCore;
using RimMult.ServerCore;
using RimMult.Shared.Coop;
using RimMult.Shared.Mods;
using RimMult.Shared.Packets;
using RimMult.Shared.Serialization;
using RimMult.Shared.Time;

namespace RimMult.Tests;

public class CoopTests
{
    private sealed class Host
    {
        public readonly TransportHub Hub = new();
        public readonly GameServer Server;
        public readonly LoopbackEndpoint Loopback;
        private double _time;

        public Host(GameMode mode)
        {
            Server = new GameServer(new ServerSettings { Mode = mode }, Hub);
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

    private static List<(int From, CoopChannel Channel, byte[] Data)> Record(ClientSession session)
    {
        var received = new List<(int, CoopChannel, byte[])>();
        session.CoopReceived += (from, channel, data) => received.Add((from, channel, data));
        return received;
    }

    [Fact]
    public void BatchRoundTrips()
    {
        var batch = new CoopBatch
        {
            Tick = 123456,
            Maps =
            {
                new MapDelta
                {
                    MapId = 3,
                    Positions = { new PawnPosition(10, 5, 7, 2) },
                    Despawned = { 11, 12 },
                    Things = { "<root><li Class=\"Pawn\"><id>Human10</id></li></root>" },
                    Patches = { new ThingPatch(40, 90, 75, 0.35f, -1f, 1), new ThingPatch(41, 10, 1, -1f, 120.5f, ThingPatch.NoForbid) },
                    Shots = { new CoopShot("Bullet_Revolver", 1.5f, 2.5f, 10.25f, 20f, 12, "Shot_Revolver") },
                    Designations = [new DesignationEntry("Mine", -1, 4, 9), new DesignationEntry("CutPlant", 77, 0, 0)],
                    Grids = "<root><topGrid>AAAA</topGrid></root>",
                    Zones = "<root><allZones /></root>",
                },
                new MapDelta { MapId = 4 },
            },
        };

        var copy = CoopBatch.Decode(batch.Encode());

        Assert.Equal(123456, copy.Tick);
        Assert.Equal(2, copy.Maps.Count);
        var first = copy.Maps[0];
        Assert.Equal(3, first.MapId);
        var position = Assert.Single(first.Positions);
        Assert.Equal((10, 5, 7, (byte)2), (position.ThingId, position.X, position.Z, position.Rotation));
        Assert.Equal([11, 12], first.Despawned);
        Assert.Equal(batch.Maps[0].Things, first.Things);
        Assert.Equal(2, first.Patches.Count);
        var patch = first.Patches[0];
        Assert.Equal((40, 90, 75, 0.35f, -1f, (byte)1), (patch.ThingId, patch.HitPoints, patch.StackCount, patch.Growth, patch.WorkDone, patch.Forbidden));
        Assert.Equal(120.5f, first.Patches[1].WorkDone);
        var shot = Assert.Single(first.Shots);
        Assert.Equal(("Bullet_Revolver", 1.5f, 2.5f, 10.25f, 20f, 12, "Shot_Revolver"), (shot.ProjectileDef, shot.FromX, shot.FromZ, shot.ToX, shot.ToZ, shot.Ticks, shot.Sound));
        Assert.Equal(ThingPatch.NoForbid, first.Patches[1].Forbidden);
        Assert.Equal(2, first.Designations!.Count);
        Assert.Equal("CutPlant", first.Designations[1].DefName);
        Assert.Equal(77, first.Designations[1].ThingId);
        Assert.Equal(-1, first.Designations[0].ThingId);
        Assert.Equal(batch.Maps[0].Grids, first.Grids);
        Assert.Equal(batch.Maps[0].Zones, first.Zones);

        // Nothing changed on the second map: no designations, grids or zones.
        Assert.Null(copy.Maps[1].Designations);
        Assert.Null(copy.Maps[1].Grids);
        Assert.Null(copy.Maps[1].Zones);
    }

    [Fact]
    public void PositionsSplitIntoSmallSelfContainedFrames()
    {
        var many = Enumerable.Range(1, 300).Select(i => new PawnPosition(100_000 + i, i % 250, i / 2, (byte)(i % 4))).ToList();
        var maps = new List<(int, List<PawnPosition>)> { (7, many), (8, [new PawnPosition(5, 1, 2, 3)]) };

        var frames = PositionsFrame.Split(4242, maps, maxBytes: 900);

        Assert.True(frames.Count > 1);
        Assert.All(frames, f => Assert.True(f.Length <= 900, $"frame of {f.Length} bytes"));
        var decoded = frames.Select(PositionsFrame.Decode).ToList();
        Assert.All(decoded, f => Assert.Equal(4242, f.Tick));
        var all = decoded.SelectMany(f => f.Maps).ToList();
        Assert.Equal(many, all.Where(m => m.MapId == 7).SelectMany(m => m.Positions).ToList());
        var single = Assert.Single(all.Where(m => m.MapId == 8).SelectMany(m => m.Positions));
        Assert.Equal((5, 1, 2, (byte)3), (single.ThingId, single.X, single.Z, single.Rotation));
    }

    [Fact]
    public void AimsAndProgressBarsTravelInTheirOwnFrame()
    {
        var frames = PositionsFrame.Split(7, [(1, [new PawnPosition(3, 4, 5, 1)])], 900,
            [new AimMark(1, 3, 1050, 2025, 90)], [new ProgressMark(1, 450, 560, 42)]);

        var decoded = frames.Select(PositionsFrame.Decode).ToList();
        var marks = Assert.Single(decoded, f => f.HasMarks);
        var aim = Assert.Single(marks.Aims);
        Assert.Equal((1, 3, 1050, 2025, 90), (aim.MapId, aim.ShooterId, aim.TargetX, aim.TargetZ, aim.Degrees));
        var bar = Assert.Single(marks.Bars);
        Assert.Equal((450, 560, (byte)42), (bar.X, bar.Z, bar.Percent));
        Assert.Single(decoded.Where(f => !f.HasMarks).SelectMany(f => f.Maps));

        Assert.Equal([5, 9, -3], CoopIds.Decode(CoopIds.Encode([5, 9, -3])));
    }

    [Fact]
    public void HostPositionsReachGuests()
    {
        var host = new Host(GameMode.Coop);
        var a = host.Join("Host", 100);
        host.Pump(a);
        var b = host.Join("B", 200);
        host.Pump(a, b);
        var atB = Record(b);

        var frame = new PositionsFrame { Tick = 9, Maps = { (1, [new PawnPosition(3, 4, 5, 1, 437, 512)]) } }.Encode();
        a.SendCoop(CoopChannel.Positions, frame);
        host.Pump(a, b);

        var got = Assert.Single(atB);
        Assert.Equal(CoopChannel.Positions, got.Channel);
        var decoded = PositionsFrame.Decode(got.Data);
        Assert.Equal(9, decoded.Tick);
        var position = decoded.Maps.Single().Positions.Single();
        Assert.Equal((4, 5, 437, 512), (position.X, position.Z, position.DrawX, position.DrawZ));
    }

    [Fact]
    public void WorldPartsRoundTrip()
    {
        var batch = new CoopBatch
        {
            Tick = 5,
            World = new CoopWorld
            {
                Factions = [new FactionStanding("Faction_3", -80, 0), new FactionStanding("Faction_7", 45, 2)],
                Research = [("Electricity", 812.5f)],
                CurrentResearch = "",
                Letters = { new CoopLetter { Label = "Raid", Text = "Pirates!", Def = "ThreatBig", MapId = 1, X = 4, Z = 9 } },
            },
        };

        var world = CoopBatch.Decode(batch.Encode()).World;

        Assert.Equal(2, world.Factions!.Count);
        Assert.Equal(("Faction_3", -80, (byte)0), (world.Factions[0].FactionId, world.Factions[0].Goodwill, world.Factions[0].Kind));
        Assert.Equal([("Electricity", 812.5f)], world.Research!);
        Assert.Equal("", world.CurrentResearch);
        var letter = Assert.Single(world.Letters);
        Assert.Equal(("Raid", "Pirates!", "ThreatBig", 1, 4, 9), (letter.Label, letter.Text, letter.Def, letter.MapId, letter.X, letter.Z));

        // Nothing changed: nothing but the header goes out.
        var empty = CoopBatch.Decode(new CoopBatch { Tick = 6 }.Encode()).World;
        Assert.True(empty.IsEmpty);
        Assert.Null(empty.Factions);
        Assert.Null(empty.CurrentResearch);
    }

    [Fact]
    public void CommandRoundTrips()
    {
        var command = new CoopCommand
        {
            Kind = CoopCommandKind.Designate,
            MapId = 2,
            ThingIds = { 5 },
            Cells = { 1, 2, 3, 4 },
            Name = "RimWorld.Designator_Build",
            Detail = "Wall",
            Extra = "WoodLog",
            Number = 1,
            X = 1.5f,
            Z = -2.25f,
            Queue = true,
        };

        var copy = CoopCommand.Decode(command.Encode());

        Assert.Equal(CoopCommandKind.Designate, copy.Kind);
        Assert.Equal(2, copy.MapId);
        Assert.Equal([5], copy.ThingIds);
        Assert.Equal([1, 2, 3, 4], copy.Cells);
        Assert.Equal(("RimWorld.Designator_Build", "Wall", "WoodLog"), (copy.Name, copy.Detail, copy.Extra));
        Assert.Equal(1, copy.Number);
        Assert.Equal((1.5f, -2.25f), (copy.X, copy.Z));
        Assert.True(copy.Queue);
    }

    [Fact]
    public void EditCarriesItsSavedState()
    {
        var xml = "<root><bills><li Class=\"Bill_Production\"><recipe>Make_Stew</recipe></li></bills></root>";
        var copy = CoopCommand.Decode(new CoopCommand { Kind = CoopCommandKind.Edit, MapId = 1, ThingIds = { 42 }, Name = "bills", Extra = xml }.Encode());

        Assert.Equal(CoopCommandKind.Edit, copy.Kind);
        Assert.Equal("bills", copy.Name);
        Assert.Equal(xml, copy.Extra);
        Assert.False(copy.Queue);
    }

    [Fact]
    public void AreasAndPoliciesRoundTrip()
    {
        var batch = new CoopBatch
        {
            Tick = 3,
            Maps = { new MapDelta { MapId = 1, Areas = "<root><areas/></root>" } },
            World = new CoopWorld { Policies = "<root><outfits/></root>", Quests = "<root><quests/></root>" },
        };

        var copy = CoopBatch.Decode(batch.Encode());

        Assert.Equal("<root><areas/></root>", copy.Maps[0].Areas);
        Assert.Equal("<root><outfits/></root>", copy.World.Policies);
        Assert.Equal("<root><quests/></root>", copy.World.Quests);
        Assert.False(copy.World.IsEmpty);
        Assert.Null(CoopBatch.Decode(new CoopBatch { Maps = { new MapDelta() } }.Encode()).Maps[0].Areas);
    }

    [Fact]
    public void GlobeRoundTrips()
    {
        var world = new CoopWorld
        {
            WorldObjects = { "<root><li Class=\"Caravan\"><ID>7</ID></li></root>" },
            RemovedWorldObjects = { 3, 9 },
        };
        world.Caravans = new List<CaravanPosition>
        {
            new() { Id = 7, Tile = "1234", NextTile = "1235", PreviousTile = "1233", CostLeft = 40.5f, CostTotal = 100f, Moving = true },
        };

        var copy = CoopBatch.Decode(new CoopBatch { World = world }.Encode()).World;

        var caravan = Assert.Single(copy.Caravans!);
        Assert.Equal((7, "1234", "1235", "1233"), (caravan.Id, caravan.Tile, caravan.NextTile, caravan.PreviousTile));
        Assert.Equal((40.5f, 100f, true, false), (caravan.CostLeft, caravan.CostTotal, caravan.Moving, caravan.Paused));
        Assert.Equal(world.WorldObjects, copy.WorldObjects);
        Assert.Equal([3, 9], copy.RemovedWorldObjects);
        Assert.False(copy.IsEmpty);
        Assert.Null(CoopBatch.Decode(new CoopBatch().Encode()).World.Caravans);
    }

    [Fact]
    public void FormCaravanCommandRoundTrips()
    {
        var command = new CoopCommand { Kind = CoopCommandKind.FormCaravan, MapId = 1, Cells = { 512, 1, 77, 35 }, Detail = "4021" };
        var copy = CoopCommand.Decode(command.Encode());
        Assert.Equal(CoopCommandKind.FormCaravan, copy.Kind);
        Assert.Equal([512, 1, 77, 35], copy.Cells);
        Assert.Equal("4021", copy.Detail);
    }

    [Fact]
    public void UnknownCommandIsRejected()
    {
        var data = new CoopCommand { Kind = CoopCommandKind.Research }.Encode();
        data[0] = 99;
        Assert.Throws<ProtocolException>(() => CoopCommand.Decode(data));
    }

    [Fact]
    public void WelcomeTellsTheMode()
    {
        var host = new Host(GameMode.Coop);
        var a = host.Join("A", 100);
        host.Pump(a);
        Assert.Equal(GameMode.Coop, a.Mode);

        var separate = new Host(GameMode.SeparateColonies);
        var b = separate.Join("B", 200);
        separate.Pump(b);
        Assert.Equal(GameMode.SeparateColonies, b.Mode);
    }

    [Fact]
    public void GuestJoinHoldsTheWorldUntilReady()
    {
        var host = new Host(GameMode.Coop);
        var a = host.Join("Host", 100);
        host.Pump(a);
        var b = host.Join("Guest", 200);
        host.Pump(a, b);
        var toHost = Record(a);
        var toGuest = Record(b);

        a.VoteSpeed(GameSpeed.Normal);
        host.Pump(a, b);
        Assert.Equal(GameSpeed.Normal, host.Server.Time.ResolveSpeed());

        // The guest asks for the game: the host is told who asks, and the world stops while the guest loads.
        b.SendCoop(CoopChannel.JoinRequest, []);
        host.Pump(a, b);
        var request = Assert.Single(toHost);
        Assert.Equal((b.PlayerId, CoopChannel.JoinRequest), (request.From, request.Channel));
        Assert.Equal(GameSpeed.Paused, host.Server.Time.ResolveSpeed());

        // Nobody can vote the hold away.
        a.VoteSpeed(GameSpeed.Fast);
        host.Pump(a, b);
        Assert.Equal(GameSpeed.Paused, host.Server.Time.ResolveSpeed());

        // The game goes to that guest only, stamped as coming from the host.
        a.SendCoop(CoopChannel.Game, [1, 2, 3], b.PlayerId);
        host.Pump(a, b);
        var game = Assert.Single(toGuest);
        Assert.Equal((a.PlayerId, CoopChannel.Game), (game.From, game.Channel));
        Assert.Equal([1, 2, 3], game.Data);

        // Loaded: time runs again and the guest counts as playing.
        b.SendCoop(CoopChannel.Ready, []);
        host.Pump(a, b);
        Assert.Equal(GameSpeed.Fast, host.Server.Time.ResolveSpeed());
        Assert.True(a.Players.Single(p => p.Id == b.PlayerId).InWorld);
        Assert.Equal(CoopChannel.Ready, toHost.Last().Channel);
    }

    [Fact]
    public void HostBroadcastReachesEveryGuestButNotTheHost()
    {
        var host = new Host(GameMode.Coop);
        var a = host.Join("Host", 100);
        host.Pump(a);
        var b = host.Join("B", 200);
        var c = host.Join("C", 300);
        host.Pump(a, b, c);
        var atHost = Record(a);
        var atB = Record(b);
        var atC = Record(c);

        a.SendCoop(CoopChannel.State, [9]);
        host.Pump(a, b, c);

        Assert.Empty(atHost);
        Assert.Equal(a.PlayerId, Assert.Single(atB).From);
        Assert.Equal(a.PlayerId, Assert.Single(atC).From);
    }

    [Fact]
    public void GuestsCanOnlyTalkToTheHost()
    {
        var host = new Host(GameMode.Coop);
        var a = host.Join("Host", 100);
        host.Pump(a);
        var b = host.Join("B", 200);
        var c = host.Join("C", 300);
        host.Pump(a, b, c);
        var atHost = Record(a);
        var atC = Record(c);

        // Addressed to another guest: still only the host gets it.
        b.SendCoop(CoopChannel.Command, [7], c.PlayerId);
        host.Pump(a, b, c);

        Assert.Empty(atC);
        Assert.Equal((b.PlayerId, CoopChannel.Command), (Assert.Single(atHost).From, atHost[0].Channel));
    }

    [Fact]
    public void GuestLeavingWhileLoadingReleasesTheHold()
    {
        var host = new Host(GameMode.Coop);
        var a = host.Join("Host", 100);
        host.Pump(a);
        var b = host.Join("B", 200);
        host.Pump(a, b);
        a.VoteSpeed(GameSpeed.Normal);
        b.SendCoop(CoopChannel.JoinRequest, []);
        host.Pump(a, b);
        Assert.Equal(GameSpeed.Paused, host.Server.Time.ResolveSpeed());

        b.Disconnect();
        host.Pump(a, b);
        Assert.Equal(GameSpeed.Normal, host.Server.Time.ResolveSpeed());

        // Same for giving up through LeaveWorld (host not ready, cancel).
        var c = host.Join("C", 300);
        host.Pump(a, c);
        c.SendCoop(CoopChannel.JoinRequest, []);
        host.Pump(a, c);
        Assert.Equal(GameSpeed.Paused, host.Server.Time.ResolveSpeed());
        c.LeaveWorld();
        host.Pump(a, c);
        Assert.Equal(GameSpeed.Normal, host.Server.Time.ResolveSpeed());
    }

    [Fact]
    public void CoopMessagesAreIgnoredInSeparateColonies()
    {
        var host = new Host(GameMode.SeparateColonies);
        var a = host.Join("Host", 100);
        host.Pump(a);
        var b = host.Join("B", 200);
        host.Pump(a, b);
        var atHost = Record(a);
        a.VoteSpeed(GameSpeed.Normal);

        b.SendCoop(CoopChannel.JoinRequest, []);
        host.Pump(a, b);

        Assert.Empty(atHost);
        Assert.Equal(GameSpeed.Normal, host.Server.Time.ResolveSpeed());
        Assert.Equal(ClientState.Connected, b.State);
    }

    [Fact]
    public void VisitTrafficGoesBetweenTheTwoPlayersOnly()
    {
        var host = new Host(GameMode.SeparateColonies);
        var a = host.Join("Host", 100);
        host.Pump(a);
        var b = host.Join("Defender", 200);
        var c = host.Join("Attacker", 300);
        host.Pump(a, b, c);
        var atHost = Record(a);
        var atDefender = Record(b);
        var atAttacker = Record(c);
        a.VoteSpeed(GameSpeed.Normal);
        b.VoteSpeed(GameSpeed.Normal);

        c.SendCoop(CoopChannel.JoinRequest, [1], b.PlayerId);
        host.Pump(a, b, c);

        var request = Assert.Single(atDefender);
        Assert.Equal((c.PlayerId, CoopChannel.JoinRequest), (request.From, request.Channel));
        Assert.Empty(atHost);
        // The world waits while the attacker loads the defender's game.
        Assert.Equal(GameSpeed.Paused, host.Server.Time.ResolveSpeed());

        b.SendCoop(CoopChannel.Game, [7], c.PlayerId);
        host.Pump(a, b, c);
        var game = Assert.Single(atAttacker);
        Assert.Equal((b.PlayerId, CoopChannel.Game), (game.From, game.Channel));

        c.SendCoop(CoopChannel.Ready, [], b.PlayerId);
        host.Pump(a, b, c);
        Assert.Equal(GameSpeed.Normal, host.Server.Time.ResolveSpeed());
        Assert.Empty(atHost);
    }

    [Fact]
    public void CheckRoundTrips()
    {
        var check = new CoopCheck { MapId = 2, FromId = 100, ToId = 250, Things = { (100, 7), (150, -3), (249, int.MaxValue) } };
        var copy = CoopCheck.Decode(check.Encode());
        Assert.Equal((2, 100, 250), (copy.MapId, copy.FromId, copy.ToId));
        Assert.Equal(check.Things, copy.Things);
    }

    [Fact]
    public void ModStatesAndCommandsRoundTrip()
    {
        var world = CoopBatch.Decode(new CoopBatch { World = new CoopWorld { ModStates = { ("mod.a:fuel", "42"), ("wc:Foo.Bar", "<root/>") } } }.Encode()).World;
        Assert.Equal([("mod.a:fuel", "42"), ("wc:Foo.Bar", "<root/>")], world.ModStates);
        Assert.False(world.IsEmpty);

        var command = CoopCommand.Decode(new CoopCommand { Kind = CoopCommandKind.Mod, Name = "mod.a:fire", Extra = "AQID" }.Encode());
        Assert.Equal((CoopCommandKind.Mod, "mod.a:fire", "AQID"), (command.Kind, command.Name, command.Extra));
    }
}
