using System.Diagnostics;
using System.Runtime.InteropServices;
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

var transport = new LiteNetServerTransport(config.Server.MaxPlayers);
var server = new GameServer(config.Server, transport, Log);
transport.Attach(server);

if (!transport.Start(config.Port))
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
    transport.Poll();
    server.Update(clock.Elapsed.TotalSeconds);
    Thread.Sleep(5);
}

Log("Shutting down");
server.Shutdown();
transport.Poll();
transport.Stop();
return 0;

void RequestStop(PosixSignalContext context)
{
    context.Cancel = true;
    stop.Cancel();
}

static void Log(string message) => Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] {message}");
