using System;
using System.Linq;
using RimMult.ClientCore;
using RimMult.Coop;
using RimMult.Net.LiteNet;
using RimMult.ServerCore;
using RimMult.Shared;
using RimMult.Shared.Net;
using RimMult.Shared.Packets;
using RimMult.Shared.World;
using RimMult.Steam;
using RimMult.Sync;
using RimMult.UI;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace RimMult;

internal sealed class HostOptions
{
    public string ServerName = "";
    public int MaxPlayers = 10;
    public string? Password;

    /// <summary>Also listen on a UDP port, for players joining by address instead of through Steam.</summary>
    public bool OpenPort;
    public int Port = ProtocolInfo.DefaultPort;

    /// <summary>Separate colonies on one planet, or everyone in the host's colony.</summary>
    public Shared.Coop.GameMode Mode;

    /// <summary>Players may declare war on each other and raid.</summary>
    public bool AllowPvp = true;
}

/// <summary>
/// The multiplayer state of this game instance: at most one client session, plus the server when hosting.
/// The host plays through a loopback connection to its own server, exactly like everyone else.
/// Everything runs on Unity's main thread, driven by <see cref="RimMultRunner"/>.
/// </summary>
internal static class Multiplayer
{
    private static TransportHub? _hub;
    private static GameServer? _server;

    public static ClientSession? Session { get; private set; }

    /// <summary>Trades with other players over the current session.</summary>
    public static TradeManager? Trades { get; private set; }

    /// <summary>How the server keys a player's colonies and parcels: SteamID, or the session id without Steam.</summary>
    public static ulong OwnerKey(PlayerInfo player) => player.SteamId != 0 ? player.SteamId : (ulong)player.Id;

    /// <summary>Where the last join went (host SteamID or address), for "try again".</summary>
    private static ulong? _lastSteamHost;
    private static string? _lastAddress;

    public static bool CanRetry => _lastSteamHost != null || _lastAddress != null;

    public static bool IsHosting => _server != null;

    /// <summary>UDP port the hosted server listens on, or null when it is Steam-only.</summary>
    public static int? HostedPort { get; private set; }

    public static bool IsActive => Session is { State: not ClientState.Disconnected };

    public static bool Host(HostOptions options, out string error)
    {
        error = "";
        if (!SteamIntegration.Available)
        {
            error = "RimMult.SteamRequired".Translate();
            return false;
        }

        Stop();
        var settings = new ServerSettings
        {
            Name = options.ServerName.NullOrEmpty() ? "RimMult.DefaultServerName".Translate(SteamIntegration.MyName).ToString() : options.ServerName,
            MaxPlayers = Mathf.Clamp(options.MaxPlayers, 1, 64),
            Password = options.Password.NullOrEmpty() ? null : options.Password,
            // Hosting from inside the game: the host's planet is the world, friends don't get to pick another.
            HostCreatesWorld = true,
            Mode = options.Mode,
            AllowPvp = options.AllowPvp,
        };

        // Hosting from a loaded game shares its planet right away; from the main menu the first colony decides.
        var world = Current.ProgramState == ProgramState.Playing ? WorldOfCurrentGame() : null;

        var hub = new TransportHub();
        var server = new GameServer(settings, hub, message => Log.Message("[RimMult] " + message), world);
        hub.Attach(server);

        if (options.OpenPort)
        {
            var udp = new LiteNetServerEndpoint(hub, settings.MaxPlayers);
            if (!udp.Start(options.Port))
            {
                error = "RimMult.PortInUse".Translate(options.Port);
                return false;
            }
            hub.AddEndpoint(udp);
            HostedPort = options.Port;
        }

        hub.AddEndpoint(new SteamServerEndpoint(hub));
        var loopback = new LoopbackEndpoint(hub);
        hub.AddEndpoint(loopback);

        _hub = hub;
        _server = server;
        _lastSteamHost = null;
        _lastAddress = null;
        SteamIntegration.SetHosting(true);
        StartSession(loopback.CreateClient(), settings.Password);
        return true;
    }

    public static void JoinSteam(ulong hostSteamId, string? password)
    {
        if (!CanJoin())
            return;
        if (hostSteamId == SteamIntegration.MySteamId)
        {
            Messages.Message("RimMult.CannotJoinSelf".Translate(), MessageTypeDefOf.RejectInput, historical: false);
            return;
        }

        Stop();
        _lastSteamHost = hostSteamId;
        _lastAddress = null;
        StartSession(new SteamClientTransport(hostSteamId), password);
        OpenDialog();
    }

    /// <summary>Asks a friend's game which mods it plays with, without joining (to make ours the same first).</summary>
    public static void CheckModsSteam(ulong hostSteamId)
    {
        if (!CanJoin() || hostSteamId == SteamIntegration.MySteamId)
            return;
        Stop();
        _lastSteamHost = hostSteamId;
        _lastAddress = null;
        StartSession(new SteamClientTransport(hostSteamId), null, checkModsOnly: true);
        OpenDialog();
    }

    /// <summary>The same as <see cref="CheckModsSteam"/> for a server by address.</summary>
    public static bool CheckModsAddress(string address, out string error)
    {
        error = "";
        if (!CanJoin())
            return false;
        if (!ServerAddress.TryParse(address, out var host, out var port))
        {
            error = "RimMult.BadAddress".Translate();
            return false;
        }
        Stop();
        _lastAddress = address;
        _lastSteamHost = null;
        StartSession(new LiteNetClientTransport(host, port), null, checkModsOnly: true);
        return true;
    }

    public static bool JoinAddress(string address, string? password, out string error)
    {
        error = "";
        if (!CanJoin())
            return false;
        if (!ServerAddress.TryParse(address, out var host, out var port))
        {
            error = "RimMult.BadAddress".Translate();
            return false;
        }

        Stop();
        _lastAddress = address;
        _lastSteamHost = null;
        StartSession(new LiteNetClientTransport(host, port), password);
        return true;
    }

    /// <summary>Notes the last server in the settings, to join it again after the game restarts (mod list changed).</summary>
    public static void RememberForRejoin()
    {
        var settings = RimMultMod.Instance.Settings;
        settings.RejoinSteamHost = _lastSteamHost ?? 0;
        settings.RejoinAddress = _lastAddress ?? "";
        RimMultMod.Instance.WriteSettings();
    }

    /// <summary>Joins the last server again (after a wrong password or a lost connection).</summary>
    public static void Retry(string? password)
    {
        if (_lastSteamHost is { } steamHost)
            JoinSteam(steamHost, password);
        else if (_lastAddress != null)
            JoinAddress(_lastAddress, password, out _);
    }

    /// <summary>The hosted world, if this instance hosts the world with that id (saved into the host's save).</summary>
    public static WorldState? HostedWorldState(string worldId) =>
        _server?.World.Definition?.WorldId == worldId ? _server.World : null;

    /// <summary>Leaves the session and, when hosting, shuts the server down (everyone else is kicked).</summary>
    public static void Stop()
    {
        Session?.Disconnect();
        Session = null;
        Trades = null;
        WorldSync.Reset();
        CoopHost.Reset();
        CoopGuest.Leave("");

        if (_server != null)
        {
            _server.Shutdown();
            _hub!.Stop();
            SteamIntegration.SetHosting(false);
        }
        _server = null;
        _hub = null;
        HostedPort = null;
    }

    /// <summary>Host with these options as soon as the save picked in the main menu has finished loading.</summary>
    public static void HostAfterLoad(HostOptions options) => _hostAfterLoad = options;

    private static HostOptions? _hostAfterLoad;

    /// <summary>Called every frame.</summary>
    public static void Update()
    {
        ModListSync.Update();
        if (_hostAfterLoad != null && !LongEventHandler.AnyEventNowOrWaiting)
        {
            if (Current.ProgramState == ProgramState.Playing)
            {
                var options = _hostAfterLoad;
                _hostAfterLoad = null;
                if (Host(options, out var error))
                    OpenDialog();
                else
                    Messages.Message(error, MessageTypeDefOf.RejectInput, historical: false);
            }
            else if (!Find.WindowStack.Windows.Any(w => w is Dialog_SaveFileList_Load or Dialog_MessageBox))
            {
                // The load dialog (or the game's "mods changed" warning after it) was closed without loading.
                _hostAfterLoad = null;
            }
        }

        _hub?.Poll();
        _server?.Update(Time.realtimeSinceStartupAsDouble);
        Session?.Poll();
        WorldSync.Update(Session);
        if (Session != null)
            CoopHost.Update(Session);
        CoopGuest.Update(Session);
        CoopCursors.Update(Session);
        PlayerVisit.Update(Session);
    }

    public static void OpenDialog()
    {
        if (!Find.WindowStack.IsOpen<Dialog_Multiplayer>())
            Find.WindowStack.Add(new Dialog_Multiplayer());
    }

    private static bool CanJoin()
    {
        if (SteamIntegration.Available)
            return true;
        Messages.Message("RimMult.SteamRequired".Translate(), MessageTypeDefOf.RejectInput, historical: false);
        return false;
    }

    /// <summary>The world for hosting the loaded game: restored from this save if it hosted before, otherwise new.</summary>
    private static WorldState WorldOfCurrentGame()
    {
        var comp = RimMultGameComp.Instance!;
        if (comp.WorldId != null && comp.HostedWorld != null)
        {
            try
            {
                var saved = WorldState.Deserialize(Convert.FromBase64String(comp.HostedWorld));
                if (saved.Definition?.WorldId == comp.WorldId)
                {
                    // The host's own game defines the mods: after a mod update the host must not be locked out
                    // of their own world. (A dedicated server keeps them pinned.)
                    saved.ModListHash = null;
                    saved.GameVersion = null;
                    return saved;
                }
            }
            catch (Exception e)
            {
                Log.Warning($"[RimMult] Could not restore the hosted world from this save: {e.Message}");
            }
        }

        comp.WorldId ??= Guid.NewGuid().ToString("N");
        return new WorldState
        {
            Definition = WorldDefinitions.FromCurrentWorld(comp.WorldId),
            Tick = Find.TickManager.TicksAbs,
        };
    }

    private static void StartSession(IClientTransport transport, string? password, bool checkModsOnly = false)
    {
        var hello = new ClientHello
        {
            SteamId = SteamIntegration.MySteamId,
            DisplayName = SteamIntegration.MyName,
            GameVersion = VersionControl.CurrentVersionStringWithRev,
            Mods = ModList.Entries(),
            Password = password.NullOrEmpty() ? null : password,
        };

        var session = new ClientSession(transport, hello, checkModsOnly);
        session.ChatReceived += OnChat;
        WorldSync.Attach(session);
        Parcels.Attach(session);
        CoopHost.Attach(session);
        CoopGuest.Attach(session);
        DiplomacyUi.Attach(session);
        Trades = new TradeManager(session);
        Trades.Invited += OnTradeInvited;
        ChronicleUi.Attach(session);
        Session = session;
        session.Start();
    }

    /// <summary>Opens a trade with the owner of a colony (from the colony's button on the globe).</summary>
    public static void StartTrade(int partnerId)
    {
        if (Trades == null)
            return;
        if (Trades.Busy)
        {
            Messages.Message("RimMult.TradeBusy".Translate(), MessageTypeDefOf.RejectInput, historical: false);
            return;
        }
        if (Trades.Invite(partnerId) is { } trade)
            Find.WindowStack.Add(new Dialog_PlayerTrade(trade));
    }

    /// <summary>A caravan reached another player's colony and wants to trade there.</summary>
    public static void StartCaravanTrade(Caravan caravan, ulong ownerKey, string ownerName)
    {
        var partner = Session?.Players.FirstOrDefault(p => OwnerKey(p) == ownerKey && p.InWorld);
        if (Trades == null || partner == null || !WorldSync.InWorld)
        {
            Messages.Message("RimMult.CaravanTradeAway".Translate(ownerName), caravan, MessageTypeDefOf.RejectInput, historical: false);
            return;
        }
        if (Trades.Busy)
        {
            Messages.Message("RimMult.TradeBusy".Translate(), MessageTypeDefOf.RejectInput, historical: false);
            return;
        }
        if (Trades.Invite(partner.Id, caravan.ID) is { } trade)
            Find.WindowStack.Add(new Dialog_PlayerTrade(trade));
    }

    private static void OnTradeInvited(PlayerTrade trade)
    {
        // No trading with an enemy (an older client could still ask).
        var partner = Session?.Players.FirstOrDefault(p => p.Id == trade.PartnerId);
        if (partner != null && Session!.RelationWith(OwnerKey(partner)) == Shared.World.PlayerRelation.Hostile)
        {
            trade.Cancel();
            return;
        }

        var text = trade.PartnerCaravanId != 0
            ? "RimMult.TradeInviteCaravan".Translate(trade.PartnerName)
            : "RimMult.TradeInvite".Translate(trade.PartnerName);
        Find.WindowStack.Add(new Dialog_MessageBox(
            text,
            "RimMult.TradeOpen".Translate(),
            () =>
            {
                trade.Join();
                Find.WindowStack.Add(new Dialog_PlayerTrade(trade));
            },
            "RimMult.TradeDecline".Translate(),
            trade.Cancel));
    }

    private static void OnChat(ChatLine line)
    {
        // Surface chat during play when the chat window is closed, so messages aren't missed.
        if (Current.ProgramState == ProgramState.Playing && !Find.WindowStack.IsOpen<Window_Chat>()
            && line.SenderId != Session?.PlayerId)
        {
            Messages.Message($"{line.SenderName}: {line.Text}", MessageTypeDefOf.SilentInput, historical: false);
        }
    }
}

/// <summary>Pumps <see cref="Multiplayer"/> every frame, in the main menu and in game alike.</summary>
internal sealed class RimMultRunner : MonoBehaviour
{
    private void Update()
    {
        try
        {
            Multiplayer.Update();
        }
        catch (Exception e)
        {
            Log.ErrorOnce($"[RimMult] Update failed: {e}", 0x52694d75);
        }
    }

    private void OnApplicationQuit() => Multiplayer.Stop();
}
