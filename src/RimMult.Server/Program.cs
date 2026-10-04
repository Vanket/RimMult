using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using RimMult.Net.LiteNet;
using RimMult.Server;
using RimMult.ServerCore;
using RimMult.Shared;
using RimMult.Shared.World;

var configPath = args.Length >= 2 && args[0] == "--config" ? args[1] : "server.json";

ServerConfig config;
if (File.Exists(configPath))
{
    config = ServerConfig.Load(configPath);
}
else
{
    config = new ServerConfig();
    config.Save(configPath);
    Log($"No config found, wrote defaults to {Path.GetFullPath(configPath)}");
}

// The file as written (without environment overrides) is what admin commands save back.
var fileConfig = File.Exists(configPath) ? ServerConfig.Load(configPath) : config;
config.ApplyEnvironment(Environment.GetEnvironmentVariable);

var worldPath = Path.GetFullPath(config.WorldFile);
WorldState? world = null;
if (File.Exists(worldPath))
{
    world = WorldState.Deserialize(File.ReadAllBytes(worldPath));
    Log($"Loaded world from {worldPath}: {world.Colonies.Count} colonies, tick {world.Tick}");
}

// Co-op runs the host's game on the host's machine: a dedicated server has no such game to share.
if (config.Server.Mode != RimMult.Shared.Coop.GameMode.SeparateColonies)
{
    Log("Co-op mode needs a host playing in RimWorld; the dedicated server runs separate colonies.");
    config.Server.Mode = RimMult.Shared.Coop.GameMode.SeparateColonies;
}

var hub = new TransportHub();
var server = new GameServer(config.Server, hub, Log, world);
var worldDirty = false;
server.WorldChanged += () => worldDirty = true;
// Bans, admins, password, limits changed by commands are kept in server.json.
server.SettingsChanged += () =>
{
    fileConfig.Server.Banned = config.Server.Banned.ToList();
    fileConfig.Server.Admins = config.Server.Admins.ToList();
    fileConfig.Server.AllowPvp = config.Server.AllowPvp;
    fileConfig.Server.Password = config.Server.Password;
    fileConfig.Server.MaxPlayers = config.Server.MaxPlayers;
    try
    {
        fileConfig.Save(configPath);
    }
    catch (Exception e)
    {
        Log($"Could not save {configPath}: {e.Message}");
    }
};
hub.Attach(server);
var udp = new LiteNetServerEndpoint(hub, config.Server.MaxPlayers);
hub.AddEndpoint(udp);

if (!udp.Start(config.Port))
{
    Log($"Could not bind UDP port {config.Port}");
    return 1;
}

Log($"RimMult server '{config.Server.Name}' listening on UDP {config.Port} (protocol v{ProtocolInfo.Version})");

using var stop = new CancellationTokenSource();
server.AddCommand("save", "save", "save the world now", _ =>
{
    SaveWorld();
    return "World saved.";
});
server.AddCommand("backup", "backup", "copy the world into the backups folder now", _ => Backup() ?? "Nothing to back up yet.");
server.AddCommand("stop", "stop", "save and shut the server down", _ =>
{
    stop.Cancel();
    return "Stopping.";
});

// Console commands (in Docker: `docker attach <container>`, detach with Ctrl+P Ctrl+Q).
var consoleLines = new ConcurrentQueue<string>();
var consoleThread = new Thread(() =>
{
    try
    {
        while (Console.In.ReadLine() is { } line)
            consoleLines.Enqueue(line);
    }
    catch (Exception)
    {
        // No console (detached service): commands come from chat only.
    }
}) { IsBackground = true, Name = "console" };
consoleThread.Start();
Log("Type help for commands.");
// Ctrl+C and `docker stop` (SIGTERM): finish the loop and kick everyone cleanly instead of dying mid-frame.
using var sigInt = PosixSignalRegistration.Create(PosixSignal.SIGINT, RequestStop);
using var sigTerm = PosixSignalRegistration.Create(PosixSignal.SIGTERM, RequestStop);

var clock = Stopwatch.StartNew();
var lastSave = 0.0;
var lastBackup = 0.0;
var lastSavedTick = server.World.Tick;
while (!stop.IsCancellationRequested)
{
    hub.Poll();
    server.Update(clock.Elapsed.TotalSeconds);
    while (consoleLines.TryDequeue(out var command))
    {
        var answer = server.RunCommand(command);
        if (answer.Length > 0)
            Console.WriteLine(answer);
    }

    // Save right away when colonies change; the clock alone only every 30 s.
    var now = clock.Elapsed.TotalSeconds;
    if (worldDirty || (now - lastSave > 30 && server.World.Tick != lastSavedTick))
    {
        SaveWorld();
        worldDirty = false;
        lastSave = now;
        lastSavedTick = server.World.Tick;
    }

    if (config.BackupIntervalMinutes > 0 && now - lastBackup > config.BackupIntervalMinutes * 60.0)
    {
        lastBackup = now;
        if (lastSave > 0)
            Backup();
    }

    Thread.Sleep(5);
}

Log("Shutting down");
server.Shutdown();
hub.Stop();
SaveWorld();
return 0;

void SaveWorld()
{
    if (server.World.Definition == null)
        return;
    try
    {
        // Write-then-rename, so a crash mid-save never leaves a truncated world behind.
        var temp = worldPath + ".tmp";
        File.WriteAllBytes(temp, server.World.Serialize());
        File.Move(temp, worldPath, overwrite: true);
    }
    catch (Exception e)
    {
        Log($"Could not save the world to {worldPath}: {e.Message}");
    }
}

// A dated copy of the world file; the oldest ones beyond the limit go. Returns what was done, or null.
string? Backup()
{
    if (server.World.Definition == null)
        return null;
    try
    {
        SaveWorld();
        var directory = Path.GetFullPath(config.BackupDirectory);
        Directory.CreateDirectory(directory);
        var target = Path.Combine(directory, $"world-{DateTime.Now:yyyyMMdd-HHmmss}.dat");
        File.Copy(worldPath, target, overwrite: true);
        foreach (var old in Directory.GetFiles(directory, "world-*.dat").OrderByDescending(f => f).Skip(Math.Max(1, config.BackupsToKeep)))
            File.Delete(old);
        Log($"Backup: {target}");
        return $"Backed up to {target}.";
    }
    catch (Exception e)
    {
        Log($"Backup failed: {e.Message}");
        return $"Backup failed: {e.Message}";
    }
}

void RequestStop(PosixSignalContext context)
{
    context.Cancel = true;
    stop.Cancel();
}

static void Log(string message) => Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] {message}");
