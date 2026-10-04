using RimMult.ClientCore;
using RimMult.ServerCore;
using RimMult.Shared.Mods;
using RimMult.Shared.Packets;

namespace RimMult.Tests;

public class AdminTests
{
    private sealed class Host
    {
        public readonly TransportHub Hub = new();
        public readonly GameServer Server;
        public readonly LoopbackEndpoint Loopback;
        private double _time;

        public Host()
        {
            Server = new GameServer(new ServerSettings(), Hub);
            Hub.Attach(Server);
            Loopback = new LoopbackEndpoint(Hub);
            Hub.AddEndpoint(Loopback);
        }

        public ClientSession Join(string name, ulong steamId)
        {
            var session = new ClientSession(Loopback.CreateClient(), new ClientHello
            {
                SteamId = steamId,
                DisplayName = name,
                GameVersion = "1.6",
                Mods = [new ModEntry("ludeon.rimworld", "Core", "1.6", 0)],
            });
            session.Start();
            return session;
        }

        public void Pump(params ClientSession[] sessions)
        {
            for (var i = 0; i < 20; i++)
            {
                Hub.Poll();
                Server.Update(_time += 1);
                foreach (var session in sessions)
                    session.Poll();
            }
        }
    }

    [Fact]
    public void HostKicksFromChat()
    {
        var host = new Host();
        var a = host.Join("Host", 100);
        host.Pump(a);
        var b = host.Join("Bob", 200);
        host.Pump(a, b);

        a.SendChat("/kick bob griefing");
        host.Pump(a, b);

        Assert.Equal(ClientState.Disconnected, b.State);
        Assert.Equal(KickReason.Kicked, b.KickReason);
        Assert.Contains(a.Chat, line => line.SenderName == ClientSession.ServerSenderName && line.Text.Contains("Kicked Bob"));
        // The command itself isn't shown to everyone as chat.
        Assert.DoesNotContain(a.Chat, line => line.Text.StartsWith("/kick"));
    }

    [Fact]
    public void PlayersCannotUseAdminCommands()
    {
        var host = new Host();
        var a = host.Join("Host", 100);
        host.Pump(a);
        var b = host.Join("Bob", 200);
        host.Pump(a, b);

        b.SendChat("/kick host");
        b.SendChat("/players");
        host.Pump(a, b);

        Assert.Equal(ClientState.Connected, a.State);
        Assert.Contains(b.Chat, line => line.Text.Contains("Only the host and admins"));
        Assert.Contains(b.Chat, line => line.Text.Contains("#2 Bob"));
    }

    [Fact]
    public void BanKeepsAPlayerOutUntilUnbanned()
    {
        var host = new Host();
        var a = host.Join("Host", 100);
        host.Pump(a);
        var b = host.Join("Bob", 200);
        host.Pump(a, b);
        var changed = 0;
        host.Server.SettingsChanged += () => changed++;

        Assert.StartsWith("Banned Bob", host.Server.RunCommand("ban Bob"));
        host.Pump(a, b);
        Assert.Equal(KickReason.Banned, b.KickReason);

        var again = host.Join("Bob", 200);
        host.Pump(a, again);
        Assert.Equal(KickReason.Banned, again.KickReason);

        host.Server.RunCommand("unban 200");
        var back = host.Join("Bob", 200);
        host.Pump(a, back);
        Assert.Equal(ClientState.Connected, back.State);
        Assert.Equal(2, changed);
    }

    [Fact]
    public void AdminsAndSettingsCommands()
    {
        var host = new Host();
        var a = host.Join("Host", 100);
        host.Pump(a);
        var b = host.Join("Bob", 200);
        host.Pump(a, b);

        host.Server.RunCommand("admin bob");
        host.Pump(a, b);
        b.SendChat("/pvp off");
        host.Pump(a, b);
        Assert.False(host.Server.Settings.AllowPvp);
        // Everyone connected learns it right away.
        Assert.False(a.AllowPvp);
        Assert.False(b.AllowPvp);

        Assert.Equal("Password set.", host.Server.RunCommand("password secret"));
        Assert.Equal("secret", host.Server.Settings.Password);
        Assert.Equal("Up to 4 players.", host.Server.RunCommand("maxplayers 4"));
        Assert.StartsWith("Unknown command", host.Server.RunCommand("frobnicate"));
        Assert.Contains("kick <player>", host.Server.RunCommand("help"));
    }

    [Fact]
    public void ProgramCommandsCanBeAdded()
    {
        var host = new Host();
        var saved = false;
        host.Server.AddCommand("save", "save", "save the world now", _ =>
        {
            saved = true;
            return "World saved.";
        });
        Assert.Equal("World saved.", host.Server.RunCommand("save"));
        Assert.True(saved);
    }
}
