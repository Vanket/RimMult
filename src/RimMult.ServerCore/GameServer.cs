using System;
using System.Collections.Generic;
using System.Linq;
using RimMult.Shared;
using RimMult.Shared.Mods;
using RimMult.Shared.Net;
using RimMult.Shared.Packets;
using RimMult.Shared.Serialization;
using RimMult.Shared.Time;
using RimMult.Shared.World;

namespace RimMult.ServerCore;

/// <summary>
/// Session layer of a RimMult server: handshake, roster, chat, speed votes, the shared clock and the shared world
/// (planet definition, everyone's colonies, world time).
/// Single-threaded: the owner calls every method from one thread (the transport's poll loop).
/// </summary>
public sealed partial class GameServer
{
    private readonly IServerTransport _transport;
    private readonly Action<string> _log;
    private readonly Dictionary<int, Session> _sessions = new();
    private List<ModEntry>? _requiredMods;
    private int _nextPlayerId = 1;
    private double _lastGrantTime = double.NegativeInfinity;

    public GameServer(ServerSettings settings, IServerTransport transport, Action<string>? log = null, WorldState? world = null)
    {
        Settings = settings;
        _transport = transport;
        _log = log ?? (_ => { });
        Time = new TimeCoordinator(settings.Time);
        World = world ?? new WorldState();
        Diplomacy = new DiplomacyBook(World.Relations, World.Treaties, () => World.NextTreatyId++);
        Settings.ModListHash ??= World.ModListHash;
        Settings.GameVersion ??= World.GameVersion;
    }

    /// <summary>Raised when the world definition or the colonies change (not on every clock tick); a cue to persist.</summary>
    public event Action? WorldChanged;

    public ServerSettings Settings { get; }

    public TimeCoordinator Time { get; }

    public WorldState World { get; }

    public DiplomacyBook Diplomacy { get; }

    public IEnumerable<PlayerInfo> Players => _sessions.Values.Where(s => s.Player != null).Select(s => s.Player!);

    public int PlayerCount => _sessions.Values.Count(s => s.Player != null);

    public void OnConnected(int connectionId)
    {
        _sessions[connectionId] = new Session(connectionId);
    }

    public void OnDisconnected(int connectionId)
    {
        if (!_sessions.TryGetValue(connectionId, out var session))
            return;

        _sessions.Remove(connectionId);
        if (session.Player is not { } player)
            return;

        _log($"{player.Name} ({player.Id}) left");
        Time.RemovePlayer(player.Id);
        Diplomacy.ForgetProposals(OwnerKey(player));
        UpdateWorldTick();
        if (player.IsHost)
            PromoteNextHost();
        BroadcastPlayerList();
    }

    public void OnData(int connectionId, byte[] data, int offset, int count)
    {
        if (!_sessions.TryGetValue(connectionId, out var session))
            return;

        IPacket packet;
        try
        {
            packet = PacketCodec.Decode(data, offset, count);
        }
        catch (ProtocolException e)
        {
            Kick(session, KickReason.BadData, e.Message);
            return;
        }

        if (session.Player == null)
        {
            if (packet is ModListQuery)
            {
                // Someone checking their mods before joining: tell them what joining takes, then hang up.
                _transport.Disconnect(connectionId, PacketCodec.Encode(new ServerModList
                {
                    GameVersion = Settings.GameVersion,
                    Mods = _requiredMods,
                }));
                return;
            }
            if (packet is ClientHello hello)
                HandleHello(session, hello);
            else
                Kick(session, KickReason.BadData, $"Expected {PacketType.ClientHello}, got {packet.Type}");
            return;
        }

        var player = session.Player;
        switch (packet)
        {
            case ChatMessage chat:
                HandleChat(player, chat);
                break;
            case SpeedVote vote:
                Time.SetVote(player.Id, vote.Speed);
                break;
            case AuthorityReport report:
                // Reports from the lobby are meaningless (no colony is running there); only in-world clients drive time.
                if (player.InWorld)
                    Time.ReportAuthority(player.Id, report.Tick, report.SustainableTicksPerSecond, report.Idle);
                break;
            case WorldCreate create:
                HandleWorldCreate(session, player, create);
                break;
            case EnterWorld enter:
                HandleEnterWorld(session, player, enter);
                break;
            case LeaveWorld:
                Time.SetHold(player.Id, false);
                if (player.InWorld)
                {
                    player.InWorld = false;
                    Time.RemoveAuthority(player.Id);
                    UpdateWorldTick();
                    BroadcastPlayerList();
                }
                break;
            case MyColonies colonies:
                HandleMyColonies(player, colonies);
                break;
            case NpcLayout layout:
                HandleNpcLayout(player, layout);
                break;
            case ParcelSend parcel:
                HandleParcelSend(player, parcel);
                break;
            case ParcelAck ack:
                HandleParcelAck(player, ack);
                break;
            case SettlementDestroyed destroyed:
                HandleSettlementDestroyed(player, destroyed);
                break;
            case PlayerRelay relay:
                HandleRelay(player, relay);
                break;
            case CoopMessage coop:
                HandleCoop(player, coop);
                break;
            case DiplomacyRequest diplomacy:
                HandleDiplomacy(player, diplomacy);
                break;
            case ColonyStatsReport stats:
                HandleColonyStats(player, stats);
                break;
            case MarketAction market:
                HandleMarket(player, market);
                break;
            default:
                Kick(session, KickReason.BadData, $"Unexpected packet {packet.Type}");
                break;
        }
    }

    /// <summary>Call every frame of the server loop.</summary>
    public void Update(double nowSeconds)
    {
        if (nowSeconds - _lastGrantTime < Settings.TickGrantIntervalSeconds)
            return;

        _lastGrantTime = nowSeconds;
        if (PlayerCount == 0)
            return;

        var grant = PacketCodec.Encode(TickGrant.From(Time.ComputeGrant()));
        Broadcast(grant, DeliveryMode.UnreliableSequenced);
        UpdateWorldTick();
        CheckTreaties();
        CheckMarket();
    }

    public void Shutdown()
    {
        foreach (var session in _sessions.Values.ToList())
            Kick(session, KickReason.ServerShutdown, "Server is shutting down");
    }

    private void HandleHello(Session session, ClientHello hello)
    {
        if (hello.ProtocolVersion != ProtocolInfo.Version)
        {
            // With the server's mod list, the player can update RimMult (and the rest) from the mismatch screen.
            Kick(session, KickReason.ProtocolMismatch,
                $"Server uses protocol {ProtocolInfo.Version}, client uses {hello.ProtocolVersion}. Update RimMult.", _requiredMods);
            return;
        }

        if (hello.SteamId != 0 && Settings.Banned.Contains(hello.SteamId))
        {
            Kick(session, KickReason.Banned, "Banned from this server");
            return;
        }

        if (!string.IsNullOrEmpty(Settings.Password) && hello.Password != Settings.Password)
        {
            Kick(session, KickReason.WrongPassword, "Wrong password");
            return;
        }

        // The same Steam account again: their old connection is dead but hasn't timed out yet (game restarted,
        // network switched). The newcomer wins; refusing it would lock them out for the whole timeout.
        // SteamId 0 means "no Steam" and is only seen in local development builds.
        if (hello.SteamId != 0
            && _sessions.Values.FirstOrDefault(s => s.Player?.SteamId == hello.SteamId) is { } stale)
        {
            Kick(stale, KickReason.AlreadyConnected, "Replaced by a new connection from the same Steam account");
        }

        if (PlayerCount >= Settings.MaxPlayers)
        {
            Kick(session, KickReason.ServerFull, $"Server is full ({Settings.MaxPlayers} players)");
            return;
        }

        Settings.GameVersion ??= hello.GameVersion;
        if (hello.GameVersion != Settings.GameVersion)
        {
            Kick(session, KickReason.GameVersionMismatch,
                $"Server runs RimWorld {Settings.GameVersion}, you have {hello.GameVersion}");
            return;
        }

        var modListHash = ModListHash.Compute(hello.Mods);
        if (Settings.ModListHash == null)
        {
            Settings.ModListHash = modListHash;
            _requiredMods = hello.Mods;
        }
        if (modListHash != Settings.ModListHash)
        {
            // A hash pinned in server.json has no list behind it until a matching player joins;
            // until then all we can say is "different".
            Kick(session, KickReason.ModListMismatch, "Your mod list differs from the server's", _requiredMods);
            return;
        }
        _requiredMods ??= hello.Mods;

        var name = hello.DisplayName.Trim();
        if (name.Length == 0)
            name = "Player";
        if (name.Length > 32)
            name = name.Substring(0, 32);

        var player = new PlayerInfo
        {
            Id = _nextPlayerId++,
            SteamId = hello.SteamId,
            Name = name,
            IsHost = PlayerCount == 0,
        };
        session.Player = player;
        player.ColorIndex = ColorFor(OwnerKey(player));
        if (player.IsHost)
            Time.HostPlayerId = player.Id;

        _log($"{player.Name} ({player.Id}) joined{(player.IsHost ? " as host" : "")}");
        Send(session, new ServerWelcome
        {
            PlayerId = player.Id,
            ServerName = Settings.Name,
            IsHost = player.IsHost,
            Time = Settings.Time,
            HostCreatesWorld = Settings.HostCreatesWorld,
            Mode = Settings.Mode,
            AllowPvp = Settings.AllowPvp,
        });
        Send(session, WorldUpdatePacket());
        Send(session, ChronicleUpdatePacket(full: true, World.Chronicle));
        Send(session, MarketStatePacket());
        BroadcastPlayerList();
    }

    private void HandleWorldCreate(Session session, PlayerInfo player, WorldCreate create)
    {
        // Someone was faster (or it is not this player's call): tell them what the world really is.
        // A repeat of the create that won (same id, e.g. a double click) gets the same answer and is harmless.
        if (World.Definition != null || (Settings.HostCreatesWorld && !player.IsHost))
        {
            Send(session, WorldUpdatePacket());
            return;
        }

        World.Definition = create.Definition;
        World.Tick = create.Tick;
        World.CreatorOwner = OwnerKey(player);
        World.ModListHash = Settings.ModListHash;
        World.GameVersion = Settings.GameVersion;
        _log($"{player.Name} created the world (seed '{create.Definition.SeedString}')");
        Broadcast(PacketCodec.Encode(WorldUpdatePacket()), DeliveryMode.ReliableOrdered);
        WorldChanged?.Invoke();
        Record(ChronicleKind.WorldCreated, OwnerKey(player), text: create.Definition.SeedString);
    }

    private void HandleEnterWorld(Session session, PlayerInfo player, EnterWorld enter)
    {
        if (World.Definition == null || enter.WorldId != World.Definition.WorldId)
        {
            Send(session, WorldUpdatePacket());
            return;
        }

        // The newcomer adopts the clock of whoever is furthest behind, so nobody has to wait for them.
        var tick = Time.SlowestTick ?? World.Tick;
        player.InWorld = true;
        Time.ReportAuthority(player.Id, tick, float.MaxValue);
        Send(session, new WorldClock { Tick = tick, HasNpcLayout = World.NpcSettlements.Count > 0 });
        if (World.NpcSettlements.Count > 0)
            Send(session, new NpcLayout { Settlements = World.NpcSettlements });
        BroadcastPlayerList();
        NoteArrival(player);

        // Parcels that arrived while they were away.
        var key = OwnerKey(player);
        ForgetDueNotices(key);
        foreach (var item in World.Mail.Where(m => m.ToOwner == key))
            Send(session, new ParcelDeliver { Item = item });
    }

    private void HandleParcelSend(PlayerInfo player, ParcelSend parcel)
    {
        if (World.Definition == null || !player.InWorld)
            return;
        if (parcel.Payload.Length > MailItem.MaxPayloadBytes)
        {
            _log($"{player.Name} sent a parcel of {parcel.Payload.Length} bytes; over the limit, dropped");
            return;
        }

        var from = OwnerKey(player);
        if (ParcelAddress.IsResearch(parcel.ToTile) && Diplomacy.Get(from, parcel.ToOwner) != PlayerRelation.Allied)
        {
            _log($"Research from {player.Name} to a non-ally dropped");
            return;
        }
        var item = new MailItem
        {
            Id = World.NextMailId++,
            FromOwner = from,
            FromName = player.Name,
            ToOwner = parcel.ToOwner,
            ToTile = Truncate(parcel.ToTile, 64),
            Summary = Truncate(parcel.Summary, 1000),
            Payload = parcel.Payload,
        };

        if (ParcelAddress.IsRaid(parcel.ToTile))
        {
            // A war party only goes out to an enemy who is playing right now; otherwise it simply comes home.
            if (RaidRefusal(from, parcel.ToOwner) is { } refusal)
            {
                _log($"Raid by {player.Name} refused: {refusal}");
                item.ToOwner = from;
                item.Returned = true;
            }
        }
        else if (!ParcelAddress.IsRaidReturn(parcel.ToTile) && !ParcelAddress.IsHelpReturn(parcel.ToTile)
                 && !World.Colonies.Any(c => c.OwnerSteamId == parcel.ToOwner))
        {
            // Nobody has a colony there anymore (abandoned, or a stale target): send it back rather than lose it.
            // (Raiders and helpers going home always reach their owner, whenever they next play.)
            item.ToOwner = from;
            item.ToTile = "";
            item.Returned = true;
        }

        World.Mail.Add(item);
        WorldChanged?.Invoke();
        _log($"Parcel {item.Id} from {player.Name}{(item.Returned ? " returned to sender" : "")}: {item.Summary}");
        NoteParcel(item, parcel.ToTile);
        if (!item.Returned && ParcelAddress.TryParseTribute(parcel.ToTile, out var treatyId, out var amount))
            NoteTribute(from, item.ToOwner, treatyId, amount);

        foreach (var session in _sessions.Values)
        {
            if (session.Player is { InWorld: true } target && OwnerKey(target) == item.ToOwner)
                Send(session, new ParcelDeliver { Item = item });
        }
    }

    private void HandleParcelAck(PlayerInfo player, ParcelAck ack)
    {
        var key = OwnerKey(player);
        if (World.Mail.RemoveAll(m => m.Id == ack.Id && m.ToOwner == key) > 0)
            WorldChanged?.Invoke();
    }

    private void HandleMyColonies(PlayerInfo player, MyColonies packet)
    {
        if (World.Definition == null || !player.InWorld)
            return;

        // Colonies are keyed by owner, so a player who reconnects (new player id) keeps their colonies.
        var key = OwnerKey(player);
        var before = World.Colonies.Where(c => c.OwnerSteamId == key).ToList();
        World.Colonies.RemoveAll(c => c.OwnerSteamId == key);
        foreach (var colony in packet.Colonies)
        {
            World.Colonies.Add(new ColonyInfo
            {
                OwnerSteamId = key,
                OwnerName = player.Name,
                Name = Truncate(colony.Name.Trim(), 64),
                Tile = Truncate(colony.Tile, 64),
                ColorIndex = player.ColorIndex,
            });
        }

        Broadcast(PacketCodec.Encode(WorldUpdatePacket()), DeliveryMode.ReliableOrdered);
        WorldChanged?.Invoke();
        NoteColonyChanges(key, before, World.Colonies.Where(c => c.OwnerSteamId == key).ToList());
    }

    /// <summary>
    /// The world's NPC settlements, from the one whose planet it is: the in-game host, or (dedicated server) the player
    /// who created the world — the first player in the world for worlds that don't remember their creator. Taken once.
    /// </summary>
    private void HandleNpcLayout(PlayerInfo player, NpcLayout layout)
    {
        if (World.Definition == null || !player.InWorld || World.NpcSettlements.Count > 0 || layout.Settlements.Count == 0)
            return;
        var allowed = Settings.HostCreatesWorld
            ? player.IsHost
            : World.CreatorOwner == 0 || World.CreatorOwner == OwnerKey(player);
        if (!allowed)
            return;

        World.NpcSettlements = layout.Settlements;
        if (World.CreatorOwner == 0)
            World.CreatorOwner = OwnerKey(player);
        WorldChanged?.Invoke();
        _log($"{player.Name} set the world's NPC settlements ({layout.Settlements.Count})");
        foreach (var session in _sessions.Values)
        {
            if (session.Player is { InWorld: true } other && other != player)
                Send(session, layout);
        }
    }

    /// <summary>Steam id identifies a player across sessions; without Steam (tests, dev builds) fall back to the session's player id.</summary>
    private static ulong OwnerKey(PlayerInfo player) => player.SteamId != 0 ? player.SteamId : (ulong)player.Id;

    private static string Truncate(string text, int length) => text.Length <= length ? text : text.Substring(0, length);

    private void UpdateWorldTick()
    {
        if (Time.SlowestTick is { } slowest && slowest > World.Tick)
            World.Tick = slowest;
    }

    private WorldUpdate WorldUpdatePacket() => new()
    {
        Definition = World.Definition,
        Colonies = World.Colonies,
        DestroyedSettlements = World.DestroyedSettlements,
        Relations = World.Relations,
        Treaties = World.Treaties,
    };

    /// <summary>Why a raid from <paramref name="attacker"/> on <paramref name="defender"/> can't happen, or null if it can.</summary>
    private string? RaidRefusal(ulong attacker, ulong defender)
    {
        if (!Settings.AllowPvp)
            return "PvP is off on this server";
        if (Diplomacy.Get(attacker, defender) != PlayerRelation.Hostile)
            return "not at war";
        if (!Players.Any(p => p.InWorld && OwnerKey(p) == defender))
            return "the defender is not playing";
        return null;
    }

    /// <summary>
    /// War, peace and alliances. Relation changes are announced to everyone; a proposal only to the player it is
    /// for, and a refusal only to the player who proposed.
    /// </summary>
    private void HandleDiplomacy(PlayerInfo player, DiplomacyRequest request)
    {
        var from = OwnerKey(player);
        var target = Players.FirstOrDefault(p => OwnerKey(p) == request.Target);
        // Proposals need someone to answer them; war can be declared on anyone with a colony.
        var targetKnown = target != null || World.Colonies.Any(c => c.OwnerSteamId == request.Target);
        var needsOnline = request.Action is DiplomacyAction.ProposePeace or DiplomacyAction.ProposeAlliance
            or DiplomacyAction.ProposePact or DiplomacyAction.DemandTribute;
        if (!targetKnown || (needsOnline && target == null))
            return;

        var happened = Diplomacy.Apply(from, request.Target, request.Action, Settings.AllowPvp, World.Tick, request.Terms);
        if (happened is not { } kind)
            return;
        var treaty = Diplomacy.LastTreaty;

        var notice = new DiplomacyNotice
        {
            From = from,
            FromName = player.Name,
            To = request.Target,
            ToName = target?.Name ?? NameOfOwner(request.Target),
            Event = kind,
            Terms = Diplomacy.LastTerms,
            TreatyId = treaty?.Id ?? 0,
        };
        _log($"Diplomacy: {notice.FromName} → {notice.ToName}: {kind}");

        switch (kind)
        {
            case DiplomacyEvent.PeaceProposed or DiplomacyEvent.AllianceProposed or DiplomacyEvent.PactProposed
                or DiplomacyEvent.TributeDemanded or DiplomacyEvent.ProposalDeclined:
                foreach (var session in _sessions.Values)
                {
                    if (session.Player is { } p && OwnerKey(p) == request.Target)
                        Send(session, notice);
                }
                break;
            default:
                WorldChanged?.Invoke();
                Broadcast(PacketCodec.Encode(WorldUpdatePacket()), DeliveryMode.ReliableOrdered);
                Broadcast(PacketCodec.Encode(notice), DeliveryMode.ReliableOrdered);
                NoteDiplomacy(kind, from, request.Target, notice.Terms, treaty);
                // A tribute starts with its first payment.
                if (treaty is { Kind: TreatyKind.Tribute })
                    CheckTreaties();
                break;
        }
    }

    /// <summary>A player's color: kept from earlier sessions, otherwise the first one nobody has yet.</summary>
    private byte ColorFor(ulong owner)
    {
        if (World.PlayerColors.TryGetValue(owner, out var existing))
            return existing;

        // netstandard2.0 has no FirstOrDefault(predicate, default); all colors taken → reuse in order.
        var used = new HashSet<byte>(World.PlayerColors.Values);
        var free = Enumerable.Range(0, ProtocolInfo.PlayerPaletteSize).Where(i => !used.Contains((byte)i)).ToList();
        var color = (byte)(free.Count > 0 ? free[0] : World.PlayerColors.Count % ProtocolInfo.PlayerPaletteSize);
        World.PlayerColors[owner] = color;
        WorldChanged?.Invoke();
        return color;
    }

    private void HandleSettlementDestroyed(PlayerInfo player, SettlementDestroyed destroyed)
    {
        const int maxDestroyed = 10_000;
        if (World.Definition == null || !player.InWorld || World.DestroyedSettlements.Count >= maxDestroyed)
            return;
        var tile = Truncate(destroyed.Tile, 64);
        if (World.DestroyedSettlements.Contains(tile))
            return;

        World.DestroyedSettlements.Add(tile);
        Broadcast(PacketCodec.Encode(WorldUpdatePacket()), DeliveryMode.ReliableOrdered);
        WorldChanged?.Invoke();
        StatsOf(OwnerKey(player)).SettlementsDestroyed++;
        Record(ChronicleKind.SettlementDestroyed, OwnerKey(player), text: tile);
    }

    /// <summary>
    /// Co-op traffic: guests talk only to the host, the host to one guest or all of them. While a guest loads the
    /// game the world is held paused, so it doesn't run away from the copy being loaded.
    /// </summary>
    private void HandleCoop(PlayerInfo sender, CoopMessage message)
    {
        if (Settings.Mode != Shared.Coop.GameMode.Coop)
        {
            HandleVisit(sender, message);
            return;
        }

        if (sender.IsHost)
        {
            var data = PacketCodec.Encode(new CoopMessage { PlayerId = sender.Id, Channel = message.Channel, Data = message.Data });
            var mode = ModeFor(message.Channel);
            foreach (var session in _sessions.Values)
            {
                if (session.Player is { } guest && !guest.IsHost && (message.PlayerId == -1 || guest.Id == message.PlayerId))
                    _transport.Send(session.ConnectionId, data, mode);
            }
            return;
        }

        switch (message.Channel)
        {
            case Shared.Coop.CoopChannel.JoinRequest:
                Time.SetHold(sender.Id, true);
                break;
            case Shared.Coop.CoopChannel.Ready:
                Time.SetHold(sender.Id, false);
                sender.InWorld = true;
                BroadcastPlayerList();
                break;
        }

        var host = _sessions.Values.FirstOrDefault(s => s.Player is { IsHost: true });
        if (host != null)
            _transport.Send(host.ConnectionId, PacketCodec.Encode(new CoopMessage { PlayerId = sender.Id, Channel = message.Channel, Data = message.Data }), ModeFor(message.Channel));
    }

    private static DeliveryMode ModeFor(Shared.Coop.CoopChannel channel) =>
        Shared.Coop.CoopChannels.IsUnreliable(channel) ? DeliveryMode.UnreliableSequenced : DeliveryMode.ReliableOrdered;

    /// <summary>
    /// Separate colonies: a visit (a player fighting their raid live in the defender's game). The same co-op traffic,
    /// but between two players only: each message names the other one and is passed on stamped with the sender.
    /// The world is held paused while the visitor loads the game it visits.
    /// </summary>
    private void HandleVisit(PlayerInfo sender, CoopMessage message)
    {
        if (message.PlayerId == sender.Id)
            return;
        var target = _sessions.Values.FirstOrDefault(s => s.Player is { } p && p.Id == message.PlayerId);
        if (target == null)
            return;

        switch (message.Channel)
        {
            case Shared.Coop.CoopChannel.JoinRequest:
                Time.SetHold(sender.Id, true);
                break;
            case Shared.Coop.CoopChannel.Ready:
            case Shared.Coop.CoopChannel.NotReady:
                // Loaded, or the visited player refused: either way nobody is loading any more.
                Time.SetHold(message.Channel == Shared.Coop.CoopChannel.Ready ? sender.Id : target.Player!.Id, false);
                break;
        }

        var mode = ModeFor(message.Channel);
        _transport.Send(target.ConnectionId, PacketCodec.Encode(new CoopMessage { PlayerId = sender.Id, Channel = message.Channel, Data = message.Data }), mode);
    }

    /// <summary>Passes a message to another player in the world, stamped with the real sender.</summary>
    private void HandleRelay(PlayerInfo sender, PlayerRelay relay)
    {
        if (!sender.InWorld || relay.PlayerId == sender.Id)
            return;
        var target = _sessions.Values.FirstOrDefault(s => s.Player is { InWorld: true } p && p.Id == relay.PlayerId);
        if (target != null)
            Send(target, new PlayerRelay { PlayerId = sender.Id, Channel = relay.Channel, Data = relay.Data });
    }

    private void HandleChat(PlayerInfo sender, ChatMessage chat)
    {
        var text = chat.Text.Trim();
        if (text.Length == 0)
            return;
        if (text.Length > ChatMessage.MaxLength)
            text = text.Substring(0, ChatMessage.MaxLength);

        // "/command": answered to the sender only.
        if (text.StartsWith("/", StringComparison.Ordinal))
        {
            var reply = RunCommand(text.Substring(1), sender);
            var session = _sessions.Values.FirstOrDefault(s => s.Player == sender);
            if (session != null && reply.Length > 0)
                Reply(session, reply);
            return;
        }

        var line = new ChatMessage { SenderId = sender.Id, Text = text, Scope = chat.Scope, TargetId = chat.TargetId };
        var me = _sessions.Values.FirstOrDefault(s => s.Player == sender);
        switch (chat.Scope)
        {
            case ChatScope.Allies:
                var owner = OwnerKey(sender);
                var allies = _sessions.Values.Where(s => s.Player is { } p && (p == sender || Diplomacy.Get(owner, OwnerKey(p)) == PlayerRelation.Allied)).ToList();
                if (allies.Count < 2 && me != null)
                {
                    Reply(me, "No ally of yours is here.");
                    return;
                }
                foreach (var session in allies)
                    Send(session, line);
                break;
            case ChatScope.Whisper:
                var to = _sessions.Values.FirstOrDefault(s => s.Player is { } p && p.Id == chat.TargetId && p != sender);
                if (to == null)
                {
                    if (me != null)
                        Reply(me, "That player isn't here.");
                    return;
                }
                Send(to, line);
                if (me != null)
                    Send(me, line);
                break;
            default:
                line.TargetId = -1;
                Broadcast(PacketCodec.Encode(line), DeliveryMode.ReliableOrdered);
                break;
        }
    }

    private void PromoteNextHost()
    {
        // The longest-connected player (lowest id) takes over.
        var next = Players.OrderBy(p => p.Id).FirstOrDefault();
        Time.HostPlayerId = next?.Id ?? -1;
        if (next == null)
            return;

        next.IsHost = true;
        _log($"{next.Name} ({next.Id}) is now the host");
    }

    private void BroadcastPlayerList()
    {
        var list = new PlayerList { Players = Players.OrderBy(p => p.Id).ToList() };
        Broadcast(PacketCodec.Encode(list), DeliveryMode.ReliableOrdered);
    }

    private void Broadcast(byte[] data, DeliveryMode mode)
    {
        foreach (var session in _sessions.Values)
        {
            if (session.Player != null)
                _transport.Send(session.ConnectionId, data, mode);
        }
    }

    private void Send(Session session, IPacket packet) =>
        _transport.Send(session.ConnectionId, PacketCodec.Encode(packet), DeliveryMode.ReliableOrdered);

    private void Kick(Session session, KickReason reason, string message, List<ModEntry>? serverMods = null)
    {
        _log($"Dropping connection {session.ConnectionId}: {reason} ({message})");
        var kick = new Kick { Reason = reason, Message = message, ServerMods = serverMods };
        _transport.Disconnect(session.ConnectionId, PacketCodec.Encode(kick));
    }

    private sealed class Session
    {
        public Session(int connectionId)
        {
            ConnectionId = connectionId;
        }

        public int ConnectionId { get; }

        /// <summary>Null until the handshake succeeds.</summary>
        public PlayerInfo? Player { get; set; }
    }
}
