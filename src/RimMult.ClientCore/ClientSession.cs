using System;
using System.Collections.Generic;
using System.Linq;
using RimMult.Shared.Mods;
using RimMult.Shared.Net;
using RimMult.Shared.Packets;
using RimMult.Shared.Serialization;
using RimMult.Shared.Time;

namespace RimMult.ClientCore;

public enum ClientState
{
    Connecting,
    Handshaking,
    Connected,
    Disconnected,
}

public sealed class ChatLine
{
    public ChatLine(int senderId, string senderName, string text)
    {
        SenderId = senderId;
        SenderName = senderName;
        Text = text;
    }

    public int SenderId { get; }
    public string SenderName { get; }
    public string Text { get; }
}

/// <summary>
/// One connection to a RimMult server as seen by a player: handshake, roster, chat and the shared clock.
/// Single-threaded; call <see cref="Poll"/> every frame.
/// </summary>
public sealed class ClientSession
{
    public const int MaxChatLines = 200;

    private readonly IClientTransport _transport;
    private readonly ClientHello _hello;
    private readonly List<ChatLine> _chat = new();
    private List<PlayerInfo> _players = new();

    public ClientSession(IClientTransport transport, ClientHello hello)
    {
        _transport = transport;
        _hello = hello;
        _transport.Connected += OnConnected;
        _transport.Received += OnReceived;
        _transport.Disconnected += OnDisconnected;
    }

    public event Action? StateChanged;
    public event Action<ChatLine>? ChatReceived;
    public event Action? PlayersChanged;

    public ClientState State { get; private set; } = ClientState.Connecting;

    public int PlayerId { get; private set; } = -1;
    public string ServerName { get; private set; } = "";
    public TimeSettings? TimeSettings { get; private set; }
    public TickGrant? LastGrant { get; private set; }

    public bool IsHost => _players.Any(p => p.Id == PlayerId && p.IsHost);

    public IReadOnlyList<PlayerInfo> Players => _players;
    public IReadOnlyList<ChatLine> Chat => _chat;

    /// <summary>Why the session ended; set once <see cref="State"/> is <see cref="ClientState.Disconnected"/>.</summary>
    public string? DisconnectReason { get; private set; }

    /// <summary>Set when the server explicitly kicked us (as opposed to a lost connection).</summary>
    public KickReason? KickReason { get; private set; }

    /// <summary>What to change to match the server, if we were rejected for our mod list and the server told us its list.</summary>
    public ModListDiff? ModDiff { get; private set; }

    public void Start() => _transport.Start();

    public void Poll()
    {
        if (State != ClientState.Disconnected)
            _transport.Poll();
    }

    public void SendChat(string text)
    {
        text = text.Trim();
        if (State != ClientState.Connected || text.Length == 0)
            return;
        if (text.Length > ChatMessage.MaxLength)
            text = text.Substring(0, ChatMessage.MaxLength);
        Send(new ChatMessage { Text = text });
    }

    public void VoteSpeed(GameSpeed? speed)
    {
        if (State == ClientState.Connected)
            Send(new SpeedVote { Speed = speed });
    }

    public void Disconnect()
    {
        if (State == ClientState.Disconnected)
            return;
        _transport.Close();
        SetDisconnected("Disconnected");
    }

    public string NameOf(int playerId) => _players.FirstOrDefault(p => p.Id == playerId)?.Name ?? $"#{playerId}";

    private void OnConnected()
    {
        SetState(ClientState.Handshaking);
        Send(_hello);
    }

    private void OnReceived(byte[] data)
    {
        IPacket packet;
        try
        {
            packet = PacketCodec.Decode(data);
        }
        catch (ProtocolException e)
        {
            _transport.Close();
            SetDisconnected($"Bad data from server: {e.Message}");
            return;
        }

        switch (packet)
        {
            case ServerWelcome welcome:
                PlayerId = welcome.PlayerId;
                ServerName = welcome.ServerName;
                TimeSettings = welcome.Time;
                SetState(ClientState.Connected);
                break;
            case PlayerList list:
                _players = list.Players;
                PlayersChanged?.Invoke();
                break;
            case ChatMessage chat:
                AddChat(new ChatLine(chat.SenderId, NameOf(chat.SenderId), chat.Text));
                break;
            case TickGrant grant:
                LastGrant = grant;
                break;
            case Kick kick:
                KickReason = kick.Reason;
                DisconnectReason = kick.Message;
                if (kick.ServerMods != null)
                    ModDiff = ModListDiff.Compute(kick.ServerMods, _hello.Mods);
                break;
        }
    }

    private void OnDisconnected(string reason) => SetDisconnected(DisconnectReason ?? reason);

    private void AddChat(ChatLine line)
    {
        _chat.Add(line);
        if (_chat.Count > MaxChatLines)
            _chat.RemoveAt(0);
        ChatReceived?.Invoke(line);
    }

    private void Send(IPacket packet) => _transport.Send(PacketCodec.Encode(packet), DeliveryMode.ReliableOrdered);

    private void SetDisconnected(string reason)
    {
        DisconnectReason ??= reason;
        SetState(ClientState.Disconnected);
    }

    private void SetState(ClientState state)
    {
        if (State == state || State == ClientState.Disconnected)
            return;
        State = state;
        StateChanged?.Invoke();
    }
}
