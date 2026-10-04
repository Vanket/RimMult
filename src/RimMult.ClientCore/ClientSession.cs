using System;
using System.Collections.Generic;
using System.Linq;
using RimMult.Shared.Mods;
using RimMult.Shared.Net;
using RimMult.Shared.Packets;
using RimMult.Shared.Serialization;
using RimMult.Shared.Time;
using RimMult.Shared.World;

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
    public ChatLine(int senderId, string senderName, string text, ChatScope scope = ChatScope.All, int targetId = -1, string targetName = "")
    {
        SenderId = senderId;
        SenderName = senderName;
        Text = text;
        Scope = scope;
        TargetId = targetId;
        TargetName = targetName;
    }

    public int SenderId { get; }
    public string SenderName { get; }
    public string Text { get; }

    /// <summary>Everyone, allies only, or a whisper.</summary>
    public ChatScope Scope { get; }

    /// <summary>Whisper: who it was for.</summary>
    public int TargetId { get; }
    public string TargetName { get; }
}

/// <summary>
/// One connection to a RimMult server as seen by a player: handshake, roster, chat and the shared clock.
/// Single-threaded; call <see cref="Poll"/> every frame.
/// </summary>
public sealed class ClientSession
{
    public const int MaxChatLines = 200;

    /// <summary>How the server's own chat lines are signed.</summary>
    public const string ServerSenderName = "[Server]";

    private readonly IClientTransport _transport;
    private readonly ClientHello _hello;
    private readonly List<ChatLine> _chat = new();
    private readonly List<ChronicleEntry> _chronicle = new();
    private List<PlayerInfo> _players = new();
    private long _clockTick;

    /// <param name="checkModsOnly">Only ask the server for its mod list (see <see cref="ModListQuery"/>), don't join.</param>
    public ClientSession(IClientTransport transport, ClientHello hello, bool checkModsOnly = false)
    {
        _transport = transport;
        _hello = hello;
        IsModCheck = checkModsOnly;
        _transport.Connected += OnConnected;
        _transport.Received += OnReceived;
        _transport.Disconnected += OnDisconnected;
    }

    public event Action? StateChanged;
    public event Action<ChatLine>? ChatReceived;
    public event Action? PlayersChanged;
    public event Action? WorldChanged;

    /// <summary>The server's answer to <see cref="EnterWorld"/>: the world tick to align the local calendar to.</summary>
    public event Action<long>? ClockReceived;

    /// <summary>Entering the world: the server has no NPC settlements yet (the world's creator should send its own).</summary>
    public event Action? NpcLayoutWanted;

    /// <summary>The world's NPC settlements (on entering the world, or when its creator first sends them).</summary>
    public event Action<List<NpcSettlement>>? NpcLayoutReceived;

    /// <summary>A parcel for this player; acknowledge with <see cref="AckParcel"/> once it is safely in the game.</summary>
    public event Action<MailItem>? ParcelReceived;

    /// <summary>A relayed message from another player: (sender id, channel, data).</summary>
    public event Action<int, RelayChannel, byte[]>? RelayReceived;

    public ClientState State { get; private set; } = ClientState.Connecting;

    public int PlayerId { get; private set; } = -1;
    public string ServerName { get; private set; } = "";
    public TimeSettings? TimeSettings { get; private set; }

    /// <summary>Only the host may create the shared world (the server is hosted from inside RimWorld).</summary>
    public bool HostCreatesWorld { get; private set; }

    public Shared.Coop.GameMode Mode { get; private set; }

    /// <summary>The server lets players declare war on each other and raid.</summary>
    public bool AllowPvp { get; private set; }

    /// <summary>Wars and alliances between players (pairs not listed are neutral).</summary>
    public IReadOnlyList<RelationEntry> Relations { get; private set; } = Array.Empty<RelationEntry>();

    /// <summary>Pacts, truces and tributes between players.</summary>
    public IReadOnlyList<Treaty> Treaties { get; private set; } = Array.Empty<Treaty>();

    /// <summary>The world's time as last heard (the clock or a grant's horizon is close enough for "how long until").</summary>
    public long WorldTick => Math.Max(_clockTick, LastGrant?.HorizonTick ?? 0);

    /// <summary>This player's treaties with another player that still run.</summary>
    public IEnumerable<Treaty> TreatiesWith(ulong owner) => Treaty.Between(Treaties, MyOwnerKey, owner, WorldTick);

    /// <summary>Someone proposed, declared or made something diplomatic (to this player, or announced to all).</summary>
    public event Action<DiplomacyNotice>? DiplomacyReceived;

    /// <summary>How the server keys this player's colonies, parcels and relations: SteamID, or the session id without Steam.</summary>
    public ulong MyOwnerKey => _hello.SteamId != 0 ? _hello.SteamId : (ulong)PlayerId;

    /// <summary>This player's relation with another player (by owner key).</summary>
    public PlayerRelation RelationWith(ulong owner) => RelationEntry.Between(Relations, MyOwnerKey, owner);

    /// <summary>The world's chronicle, oldest first.</summary>
    public IReadOnlyList<ChronicleEntry> Chronicle => _chronicle;

    /// <summary>Every player's standing in the world.</summary>
    public IReadOnlyList<PlayerStats> Stats { get; private set; } = Array.Empty<PlayerStats>();

    /// <summary>The chronicle or the stats changed (anything: a full update too).</summary>
    public event Action? ChronicleChanged;

    /// <summary>New lines in the chronicle (not the full one received on joining).</summary>
    public event Action<IReadOnlyList<ChronicleEntry>>? ChronicleAdded;

    /// <summary>Co-op traffic: (sender id, channel, data).</summary>
    public event Action<int, Shared.Coop.CoopChannel, byte[]>? CoopReceived;
    public TickGrant? LastGrant { get; private set; }

    public bool IsHost => _players.Any(p => p.Id == PlayerId && p.IsHost);

    public IReadOnlyList<PlayerInfo> Players => _players;
    public IReadOnlyList<ChatLine> Chat => _chat;

    /// <summary>The shared planet; null until the first player creates it.</summary>
    public WorldDefinition? World { get; private set; }

    public IReadOnlyList<ColonyInfo> Colonies { get; private set; } = Array.Empty<ColonyInfo>();

    /// <summary>Tiles of NPC settlements destroyed by any player.</summary>
    public IReadOnlyList<string> DestroyedSettlements { get; private set; } = Array.Empty<string>();

    /// <summary>Why the session ended; set once <see cref="State"/> is <see cref="ClientState.Disconnected"/>.</summary>
    public string? DisconnectReason { get; private set; }

    /// <summary>Set when the server explicitly kicked us (as opposed to a lost connection).</summary>
    public KickReason? KickReason { get; private set; }

    /// <summary>What to change to match the server, if we were rejected for our mod list and the server told us its list.</summary>
    public ModListDiff? ModDiff { get; private set; }

    /// <summary>This session only asked for the server's mod list (it ends once the answer is in).</summary>
    public bool IsModCheck { get; }

    /// <summary>The server's protocol version, as its <see cref="ServerModList"/> said (0 before that).</summary>
    public int ServerProtocol { get; private set; }

    /// <summary>The server's mod list in load order, when it rejected ours (to make ours the same).</summary>
    public IReadOnlyList<ModEntry>? ServerMods { get; private set; }

    public void Start() => _transport.Start();

    public void Poll()
    {
        if (State != ClientState.Disconnected)
            _transport.Poll();
    }

    /// <param name="scope">Everyone, allies only, or a whisper to <paramref name="targetId"/>.</param>
    public void SendChat(string text, ChatScope scope = ChatScope.All, int targetId = -1)
    {
        text = text.Trim();
        if (State != ClientState.Connected || text.Length == 0)
            return;
        if (text.Length > ChatMessage.MaxLength)
            text = text.Substring(0, ChatMessage.MaxLength);
        Send(new ChatMessage { Text = text, Scope = scope, TargetId = targetId });
    }

    public void VoteSpeed(GameSpeed? speed)
    {
        if (State == ClientState.Connected)
            Send(new SpeedVote { Speed = speed });
    }

    public void CreateWorld(WorldDefinition definition, long tick)
    {
        if (State == ClientState.Connected)
            Send(new WorldCreate { Definition = definition, Tick = tick });
    }

    public void EnterWorld(string worldId)
    {
        if (State == ClientState.Connected)
            Send(new EnterWorld { WorldId = worldId });
    }

    public void LeaveWorld()
    {
        if (State == ClientState.Connected)
            Send(new LeaveWorld());
    }

    public void ReportAuthority(long tick, float sustainableTicksPerSecond, bool idle = false)
    {
        if (State == ClientState.Connected)
            Send(new AuthorityReport { Tick = tick, SustainableTicksPerSecond = sustainableTicksPerSecond, Idle = idle });
    }

    public void SendParcel(ulong toOwner, string toTile, string summary, byte[] payload)
    {
        if (State == ClientState.Connected)
            Send(new ParcelSend { ToOwner = toOwner, ToTile = toTile, Summary = summary, Payload = payload });
    }

    public void SendRelay(int playerId, RelayChannel channel, byte[] data)
    {
        if (State == ClientState.Connected)
            Send(new PlayerRelay { PlayerId = playerId, Channel = channel, Data = data });
    }

    /// <summary>Co-op: as a guest always to the host; as the host to <paramref name="playerId"/> or all guests (-1).</summary>
    public void SendCoop(Shared.Coop.CoopChannel channel, byte[] data, int playerId = -1)
    {
        if (State != ClientState.Connected)
            return;
        // Positions go unreliably: the next frame replaces a lost one, and nothing big queues in front of them.
        var mode = Shared.Coop.CoopChannels.IsUnreliable(channel) ? DeliveryMode.UnreliableSequenced : DeliveryMode.ReliableOrdered;
        _transport.Send(PacketCodec.Encode(new CoopMessage { PlayerId = playerId, Channel = channel, Data = data }), mode);
    }

    /// <summary>How this player's colonies are doing (for the chronicle's table); only counts while in the world.</summary>
    public void ReportColonyStats(float wealth, int colonists)
    {
        if (State == ClientState.Connected)
            Send(new ColonyStatsReport { Wealth = wealth, Colonists = colonists });
    }

    public void SendDiplomacy(ulong target, DiplomacyAction action, TreatyTerms? terms = null)
    {
        if (State == ClientState.Connected)
            Send(new DiplomacyRequest { Target = target, Action = action, Terms = terms ?? TreatyTerms.None });
    }

    public void ReportSettlementDestroyed(string tile)
    {
        if (State == ClientState.Connected)
            Send(new SettlementDestroyed { Tile = tile });
    }

    /// <summary>A player's color index (0 if unknown).</summary>
    public byte ColorOf(int playerId) => _players.FirstOrDefault(p => p.Id == playerId)?.ColorIndex ?? 0;

    public void AckParcel(long id)
    {
        if (State == ClientState.Connected)
            Send(new ParcelAck { Id = id });
    }

    /// <summary>This game's NPC settlements, as the world's layout (the server keeps the first one it may take).</summary>
    public void SendNpcLayout(List<NpcSettlement> settlements)
    {
        if (State == ClientState.Connected)
            Send(new NpcLayout { Settlements = settlements });
    }

    public void SendColonies(List<ColonyInfo> colonies)
    {
        if (State == ClientState.Connected)
            Send(new MyColonies { Colonies = colonies });
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
        if (IsModCheck)
            Send(new ModListQuery());
        else
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
                HostCreatesWorld = welcome.HostCreatesWorld;
                Mode = welcome.Mode;
                AllowPvp = welcome.AllowPvp;
                SetState(ClientState.Connected);
                break;
            case PlayerList list:
                _players = list.Players;
                PlayersChanged?.Invoke();
                break;
            case ChatMessage chat:
                // Id -1 is the server itself (announcements, answers to "/commands").
                AddChat(new ChatLine(chat.SenderId, chat.SenderId < 0 ? ServerSenderName : NameOf(chat.SenderId), chat.Text,
                    chat.Scope, chat.TargetId, chat.Scope == ChatScope.Whisper ? NameOf(chat.TargetId) : ""));
                break;
            case TickGrant grant:
                LastGrant = grant;
                break;
            case WorldUpdate world:
                World = world.Definition;
                Colonies = world.Colonies;
                DestroyedSettlements = world.DestroyedSettlements;
                Relations = world.Relations;
                Treaties = world.Treaties;
                WorldChanged?.Invoke();
                break;
            case WorldClock clock:
                _clockTick = clock.Tick;
                ClockReceived?.Invoke(clock.Tick);
                if (!clock.HasNpcLayout)
                    NpcLayoutWanted?.Invoke();
                break;
            case NpcLayout layout:
                NpcLayoutReceived?.Invoke(layout.Settlements);
                break;
            case ParcelDeliver parcel:
                ParcelReceived?.Invoke(parcel.Item);
                break;
            case PlayerRelay relay:
                RelayReceived?.Invoke(relay.PlayerId, relay.Channel, relay.Data);
                break;
            case DiplomacyNotice notice:
                DiplomacyReceived?.Invoke(notice);
                break;
            case CoopMessage coop:
                CoopReceived?.Invoke(coop.PlayerId, coop.Channel, coop.Data);
                break;
            case ServerModList modList:
                ServerProtocol = modList.ProtocolVersion;
                if (modList.Mods != null)
                {
                    ServerMods = modList.Mods;
                    ModDiff = ModListDiff.Compute(modList.Mods, _hello.Mods);
                }
                DisconnectReason ??= "Mod list received";
                break;
            case ChronicleUpdate chronicle:
                if (chronicle.Full)
                    _chronicle.Clear();
                _chronicle.AddRange(chronicle.Entries);
                if (_chronicle.Count > Shared.World.Chronicle.MaxEntries)
                    _chronicle.RemoveRange(0, _chronicle.Count - Shared.World.Chronicle.MaxEntries);
                Stats = chronicle.Stats;
                ChronicleChanged?.Invoke();
                if (!chronicle.Full && chronicle.Entries.Count > 0)
                    ChronicleAdded?.Invoke(chronicle.Entries);
                break;
            case Kick kick:
                KickReason = kick.Reason;
                DisconnectReason = kick.Message;
                if (kick.ServerMods != null)
                {
                    ServerMods = kick.ServerMods;
                    ModDiff = ModListDiff.Compute(kick.ServerMods, _hello.Mods);
                }
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
