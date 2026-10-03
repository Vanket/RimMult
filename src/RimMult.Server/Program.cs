using System.Diagnostics;
using System.Runtime.InteropServices;
using RimMult.Net.LiteNet;
using RimMult.Server;
using RimMult.ServerCore;
using RimMult.Shared;

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

var hub = new TransportHub();
var server = new GameServer(config.Server, hub, Log);
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
while (!stop.IsCancellationRequested)
{
    hub.Poll();
    server.Update(clock.Elapsed.TotalSeconds);
    Thread.Sleep(5);
}

Log("Shutting down");
server.Shutdown();
hub.Stop();
return 0;

void RequestStop(PosixSignalContext context)
{
    context.Cancel = true;
    stop.Cancel();
}

static void Log(string message) => Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] {message}");
