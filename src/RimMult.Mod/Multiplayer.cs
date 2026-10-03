using System;
using RimMult.ClientCore;
using RimMult.Net.LiteNet;
using RimMult.ServerCore;
using RimMult.Shared;
using RimMult.Shared.Net;
using RimMult.Shared.Packets;
using RimMult.Steam;
using RimMult.UI;
using RimWorld;
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
        };

        var hub = new TransportHub();
        var server = new GameServer(settings, hub, message => Log.Message("[RimMult] " + message));
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
        StartSession(new SteamClientTransport(hostSteamId), password);
        OpenDialog();
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
        StartSession(new LiteNetClientTransport(host, port), password);
        return true;
    }

    /// <summary>Leaves the session and, when hosting, shuts the server down (everyone else is kicked).</summary>
    public static void Stop()
    {
        Session?.Disconnect();
        Session = null;

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

    /// <summary>Called every frame.</summary>
    public static void Update()
    {
        _hub?.Poll();
        _server?.Update(Time.realtimeSinceStartupAsDouble);
        Session?.Poll();
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

    private static void StartSession(IClientTransport transport, string? password)
    {
        var hello = new ClientHello
        {
            SteamId = SteamIntegration.MySteamId,
            DisplayName = SteamIntegration.MyName,
            GameVersion = VersionControl.CurrentVersionStringWithRev,
            Mods = ModList.Entries(),
            Password = password.NullOrEmpty() ? null : password,
        };

        var session = new ClientSession(transport, hello);
        session.ChatReceived += OnChat;
        Session = session;
        session.Start();
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
