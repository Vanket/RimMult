using System;
using System.Collections.Generic;
using System.Linq;
using RimMult.Shared;
using RimMult.Shared.Packets;
using RimMult.Shared.Serialization;
using RimMult.Shared.Time;

namespace RimMult.ServerCore;

/// <summary>
/// Session layer of a RimMult server: handshake, roster, chat, speed votes and the shared clock.
/// Single-threaded: the owner calls every method from one thread (the transport's poll loop).
/// </summary>
public sealed class GameServer
{
    private readonly IServerTransport _transport;
    private readonly Action<string> _log;
    private readonly Dictionary<int, Session> _sessions = new();
    private int _nextPlayerId = 1;
    private double _lastGrantTime = double.NegativeInfinity;

    public GameServer(ServerSettings settings, IServerTransport transport, Action<string>? log = null)
    {
        Settings = settings;
        _transport = transport;
        _log = log ?? (_ => { });
        Time = new TimeCoordinator(settings.Time);
    }

    public ServerSettings Settings { get; }

    public TimeCoordinator Time { get; }

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
                Time.ReportAuthority(player.Id, report.Tick, report.SustainableTicksPerSecond);
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

        if (PlayerCount >= Settings.MaxPlayers)
        {
            Kick(session, KickReason.ServerFull, $"Server is full ({Settings.MaxPlayers} players)");
            return;
        }

        // SteamId 0 means "no Steam" and is only seen in local development builds.
        if (hello.SteamId != 0 && Players.Any(p => p.SteamId == hello.SteamId))
        {
            Kick(session, KickReason.AlreadyConnected, "This Steam account is already connected");
            return;
        }

        Settings.GameVersion ??= hello.GameVersion;
        if (hello.GameVersion != Settings.GameVersion)
        {
            Kick(session, KickReason.GameVersionMismatch,
                $"Server runs RimWorld {Settings.GameVersion}, you have {hello.GameVersion}");
            return;
        }

        Settings.ModListHash ??= hello.ModListHash;
        if (hello.ModListHash != Settings.ModListHash)
        {
            Kick(session, KickReason.ModListMismatch, "Your mod list differs from the server's");
            return;
        }

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
        });
        BroadcastPlayerList();
    }

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

    private void Kick(Session session, KickReason reason, string message)
    {
        _log($"Dropping connection {session.ConnectionId}: {reason} ({message})");
        Send(session, new Kick { Reason = reason, Message = message });
        _transport.Disconnect(session.ConnectionId);
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
