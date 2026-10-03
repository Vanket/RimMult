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

var worldPath = Path.GetFullPath(config.WorldFile);
WorldState? world = null;
if (File.Exists(worldPath))
{
    world = WorldState.Deserialize(File.ReadAllBytes(worldPath));
    Log($"Loaded world from {worldPath}: {world.Colonies.Count} colonies, tick {world.Tick}");
}

var hub = new TransportHub();
var server = new GameServer(config.Server, hub, Log, world);
var worldDirty = false;
server.WorldChanged += () => worldDirty = true;
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
// Ctrl+C and `docker stop` (SIGTERM): finish the loop and kick everyone cleanly instead of dying mid-frame.
using var sigInt = PosixSignalRegistration.Create(PosixSignal.SIGINT, RequestStop);
using var sigTerm = PosixSignalRegistration.Create(PosixSignal.SIGTERM, RequestStop);

var clock = Stopwatch.StartNew();
var lastSave = 0.0;
var lastSavedTick = server.World.Tick;
while (!stop.IsCancellationRequested)
{
    hub.Poll();
    server.Update(clock.Elapsed.TotalSeconds);

    // Save right away when colonies change; the clock alone only every 30 s.
    var now = clock.Elapsed.TotalSeconds;
    if (worldDirty || (now - lastSave > 30 && server.World.Tick != lastSavedTick))
    {
        SaveWorld();
        worldDirty = false;
        lastSave = now;
        lastSavedTick = server.World.Tick;
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

void RequestStop(PosixSignalContext context)
{
    context.Cancel = true;
    stop.Cancel();
}

static void Log(string message) => Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] {message}");
