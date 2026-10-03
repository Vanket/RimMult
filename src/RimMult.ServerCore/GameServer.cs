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
public sealed class GameServer
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
        Settings.ModListHash ??= World.ModListHash;
        Settings.GameVersion ??= World.GameVersion;
    }

    /// <summary>Raised when the world definition or the colonies change (not on every clock tick); a cue to persist.</summary>
    public event Action? WorldChanged;

    public ServerSettings Settings { get; }

    public TimeCoordinator Time { get; }

    public WorldState World { get; }

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
                    Time.ReportAuthority(player.Id, report.Tick, report.SustainableTicksPerSecond);
                break;
            case WorldCreate create:
                HandleWorldCreate(session, player, create);
                break;
            case EnterWorld enter:
                HandleEnterWorld(session, player, enter);
                break;
            case LeaveWorld:
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
            Kick(session, KickReason.ProtocolMismatch,
                $"Server uses protocol {ProtocolInfo.Version}, client uses {hello.ProtocolVersion}. Update RimMult.");
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
        });
        Send(session, WorldUpdatePacket());
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
        World.ModListHash = Settings.ModListHash;
        World.GameVersion = Settings.GameVersion;
        _log($"{player.Name} created the world (seed '{create.Definition.SeedString}')");
        Broadcast(PacketCodec.Encode(WorldUpdatePacket()), DeliveryMode.ReliableOrdered);
        WorldChanged?.Invoke();
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
        Send(session, new WorldClock { Tick = tick });
        BroadcastPlayerList();
    }

    private void HandleMyColonies(PlayerInfo player, MyColonies packet)
    {
        if (World.Definition == null || !player.InWorld)
            return;

        // Colonies are keyed by owner, so a player who reconnects (new player id) keeps their colonies.
        var key = OwnerKey(player);
        World.Colonies.RemoveAll(c => c.OwnerSteamId == key);
        foreach (var colony in packet.Colonies)
        {
            World.Colonies.Add(new ColonyInfo
            {
                OwnerSteamId = key,
                OwnerName = player.Name,
                Name = Truncate(colony.Name.Trim(), 64),
                Tile = Truncate(colony.Tile, 64),
            });
        }

        Broadcast(PacketCodec.Encode(WorldUpdatePacket()), DeliveryMode.ReliableOrdered);
        WorldChanged?.Invoke();
    }

    /// <summary>Steam id identifies a player across sessions; without Steam (tests, dev builds) fall back to the session's player id.</summary>
    private static ulong OwnerKey(PlayerInfo player) => player.SteamId != 0 ? player.SteamId : (ulong)player.Id;

    private static string Truncate(string text, int length) => text.Length <= length ? text : text.Substring(0, length);

    private void UpdateWorldTick()
    {
        if (Time.SlowestTick is { } slowest && slowest > World.Tick)
            World.Tick = slowest;
    }

    private WorldUpdate WorldUpdatePacket() => new() { Definition = World.Definition, Colonies = World.Colonies };

    private void HandleChat(PlayerInfo sender, ChatMessage chat)
    {
        var text = chat.Text.Trim();
        if (text.Length == 0)
            return;
        if (text.Length > ChatMessage.MaxLength)
            text = text.Substring(0, ChatMessage.MaxLength);

        Broadcast(PacketCodec.Encode(new ChatMessage { SenderId = sender.Id, Text = text }), DeliveryMode.ReliableOrdered);
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
