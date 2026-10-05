using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using RimMult.Shared.Net;
using RimMult.Shared.Packets;
using RimMult.Shared.World;

namespace RimMult.ServerCore;

/// <summary>
/// Admin commands: typed in a dedicated server's console, or in chat as "/command" by the host or an admin.
/// Anyone may use "help" and "players".
/// </summary>
public sealed partial class GameServer
{
    /// <summary>The sender id of the server's own chat lines (announcements, answers to commands).</summary>
    public const int ServerSenderId = -1;

    private sealed class Command
    {
        public Command(string usage, string help, Func<string[], PlayerInfo?, string> run, bool forEveryone = false)
        {
            Usage = usage;
            Help = help;
            Run = run;
            ForEveryone = forEveryone;
        }

        public string Usage { get; }
        public string Help { get; }
        public Func<string[], PlayerInfo?, string> Run { get; }
        public bool ForEveryone { get; }
    }

    private Dictionary<string, Command>? _commands;

    /// <summary>Settings were changed by a command (bans, admins, password…): a dedicated server saves its config.</summary>
    public event Action? SettingsChanged;

    /// <summary>Adds a command of the program running the server (save, backup, stop on a dedicated server).</summary>
    public void AddCommand(string name, string usage, string help, Func<string[], string> run) =>
        Commands()[name] = new Command(usage, help, (args, _) => run(args));

    public bool IsAdmin(PlayerInfo player) => player.IsHost || (player.SteamId != 0 && Settings.Admins.Contains(player.SteamId));

    /// <summary>Runs one command line; <paramref name="by"/> is null for the console. Returns the answer.</summary>
    public string RunCommand(string line, PlayerInfo? by = null)
    {
        var parts = line.Trim().Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
            return "";
        if (!Commands().TryGetValue(parts[0].ToLowerInvariant(), out var command))
            return $"Unknown command '{parts[0]}'. Type help.";
        if (by != null && !command.ForEveryone && !IsAdmin(by))
            return "Only the host and admins can do that.";
        try
        {
            var answer = command.Run(parts.Skip(1).ToArray(), by);
            var logged = answer.Replace('\n', ' ');
            _log($"Command '{line.Trim()}' by {by?.Name ?? "console"}: {(logged.Length > 120 ? logged.Substring(0, 120) + "…" : logged)}");
            return answer;
        }
        catch (Exception e)
        {
            return $"Command failed: {e.Message}";
        }
    }

    /// <summary>A line from the server to everyone in chat.</summary>
    public void Say(string text)
    {
        foreach (var line in Lines(text))
            Broadcast(PacketCodec.Encode(new ChatMessage { SenderId = ServerSenderId, Text = line }), DeliveryMode.ReliableOrdered);
    }

    private void Reply(Session session, string text)
    {
        foreach (var line in Lines(text))
            Send(session, new ChatMessage { SenderId = ServerSenderId, Text = line });
    }

    private static IEnumerable<string> Lines(string text) =>
        text.Split('\n').Where(l => l.Length > 0).Select(l => Truncate(l, ChatMessage.MaxLength));

    private Dictionary<string, Command> Commands() => _commands ??= new Dictionary<string, Command>
    {
        ["help"] = new("help", "this list", (_, by) => string.Join("\n", Commands()
            .Where(c => by == null || c.Value.ForEveryone || IsAdmin(by))
            .Select(c => $"{c.Value.Usage} — {c.Value.Help}")), forEveryone: true),
        ["players"] = new("players", "who is here", (_, _) => PlayerCount == 0
            ? "Nobody is connected."
            : string.Join("\n", Players.OrderBy(p => p.Id).Select(p =>
                $"#{p.Id} {p.Name}{(p.IsHost ? " [host]" : "")}{(IsAdmin(p) && !p.IsHost ? " [admin]" : "")}{(p.InWorld ? " in world" : "")}{(p.SteamId != 0 ? " steam " + p.SteamId : "")}")),
            forEveryone: true),
        ["kick"] = new("kick <player> [reason]", "disconnect a player", (args, _) =>
        {
            var (session, player) = Find(args);
            var reason = args.Length > 1 ? string.Join(" ", args.Skip(1)) : "Kicked by an admin";
            Kick(session, KickReason.Kicked, reason);
            Say($"{player.Name} was kicked: {reason}");
            return $"Kicked {player.Name}.";
        }),
        ["ban"] = new("ban <player|steamid> [reason]", "kick and keep out", (args, _) =>
        {
            Need(args, 1);
            ulong steamId;
            string name;
            if (ulong.TryParse(args[0], NumberStyles.None, CultureInfo.InvariantCulture, out var id) && id > 10_000)
            {
                steamId = id;
                name = Players.FirstOrDefault(p => p.SteamId == id)?.Name ?? id.ToString(CultureInfo.InvariantCulture);
            }
            else
            {
                var (_, player) = Find(args);
                if (player.SteamId == 0)
                    return $"{player.Name} has no SteamID to ban; use kick.";
                steamId = player.SteamId;
                name = player.Name;
            }
            if (!Settings.Banned.Contains(steamId))
                Settings.Banned.Add(steamId);
            SettingsChanged?.Invoke();
            var reason = args.Length > 1 ? string.Join(" ", args.Skip(1)) : "Banned from this server";
            foreach (var session in _sessions.Values.Where(s => s.Player?.SteamId == steamId).ToList())
                Kick(session, KickReason.Banned, reason);
            Say($"{name} was banned.");
            return $"Banned {name} ({steamId}).";
        }),
        ["unban"] = new("unban <steamid>", "let a banned player in again", (args, _) =>
        {
            Need(args, 1);
            var id = ulong.Parse(args[0], CultureInfo.InvariantCulture);
            var removed = Settings.Banned.Remove(id);
            SettingsChanged?.Invoke();
            return removed ? $"Unbanned {id}." : $"{id} wasn't banned.";
        }),
        ["bans"] = new("bans", "banned SteamIDs", (_, _) =>
            Settings.Banned.Count == 0 ? "Nobody is banned." : string.Join(", ", Settings.Banned)),
        ["admin"] = new("admin <player|steamid>", "allow admin commands in chat", (args, _) =>
        {
            var steamId = SteamIdOf(args);
            if (!Settings.Admins.Contains(steamId))
                Settings.Admins.Add(steamId);
            SettingsChanged?.Invoke();
            BroadcastPlayerList(); // the admin panel shows up for them
            return $"{steamId} is an admin.";
        }),
        ["unadmin"] = new("unadmin <player|steamid>", "take admin away", (args, _) =>
        {
            var steamId = SteamIdOf(args);
            Settings.Admins.Remove(steamId);
            SettingsChanged?.Invoke();
            BroadcastPlayerList();
            return $"{steamId} is no longer an admin.";
        }),
        ["say"] = new("say <text>", "announce to everyone", (args, _) =>
        {
            Need(args, 1);
            Say(string.Join(" ", args));
            return "Sent.";
        }),
        ["pvp"] = new("pvp on|off", "allow wars and raids between players", (args, _) =>
        {
            Need(args, 1);
            Settings.AllowPvp = OnOff(args[0]);
            SettingsChanged?.Invoke();
            ResendWelcome();
            Say(Settings.AllowPvp ? "PvP is on: players may declare war and raid." : "PvP is off.");
            return $"PvP {(Settings.AllowPvp ? "on" : "off")}.";
        }),
        ["password"] = new("password <text>|off", "set or remove the password (for new connections)", (args, _) =>
        {
            Need(args, 1);
            Settings.Password = args[0].Equals("off", StringComparison.OrdinalIgnoreCase) ? null : string.Join(" ", args);
            SettingsChanged?.Invoke();
            return Settings.Password == null ? "Password removed." : "Password set.";
        }),
        ["maxplayers"] = new("maxplayers <n>", "player limit (for new connections)", (args, _) =>
        {
            Need(args, 1);
            var max = int.Parse(args[0], CultureInfo.InvariantCulture);
            if (max < 1 || max > 64)
                return "Between 1 and 64.";
            Settings.MaxPlayers = max;
            SettingsChanged?.Invoke();
            return $"Up to {max} players.";
        }),
    };

    /// <summary>Settings that players learn when they join (PvP) go to everyone connected again.</summary>
    private void ResendWelcome()
    {
        foreach (var session in _sessions.Values)
        {
            if (session.Player is not { } player)
                continue;
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
        }
    }

    private static void Need(string[] args, int count)
    {
        if (args.Length < count)
            throw new ArgumentException("missing argument");
    }

    private static bool OnOff(string value) => value.ToLowerInvariant() switch
    {
        "on" or "1" or "true" or "yes" => true,
        "off" or "0" or "false" or "no" => false,
        _ => throw new ArgumentException("say on or off"),
    };

    /// <summary>A connected player by #id, SteamID, exact name or a name start that fits only one.</summary>
    private (Session Session, PlayerInfo Player) Find(string[] args)
    {
        Need(args, 1);
        var key = args[0].TrimStart('#');
        var connected = _sessions.Values.Where(s => s.Player != null).ToList();
        Session? found = null;
        if (int.TryParse(key, NumberStyles.None, CultureInfo.InvariantCulture, out var id))
            found = connected.FirstOrDefault(s => s.Player!.Id == id);
        if (found == null && ulong.TryParse(key, NumberStyles.None, CultureInfo.InvariantCulture, out var steamId))
            found = connected.FirstOrDefault(s => s.Player!.SteamId == steamId);
        found ??= connected.FirstOrDefault(s => string.Equals(s.Player!.Name, args[0], StringComparison.OrdinalIgnoreCase));
        if (found == null)
        {
            var starts = connected.Where(s => s.Player!.Name.StartsWith(args[0], StringComparison.OrdinalIgnoreCase)).ToList();
            if (starts.Count > 1)
                throw new ArgumentException($"'{args[0]}' fits several players");
            found = starts.FirstOrDefault();
        }
        if (found == null)
            throw new ArgumentException($"no player '{args[0]}'");
        return (found, found.Player!);
    }

    private ulong SteamIdOf(string[] args)
    {
        Need(args, 1);
        if (ulong.TryParse(args[0], NumberStyles.None, CultureInfo.InvariantCulture, out var id) && id > 10_000)
            return id;
        var (_, player) = Find(args);
        if (player.SteamId == 0)
            throw new ArgumentException($"{player.Name} has no SteamID");
        return player.SteamId;
    }
}
