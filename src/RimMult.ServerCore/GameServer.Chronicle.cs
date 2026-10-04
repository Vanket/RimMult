using System;
using System.Collections.Generic;
using System.Linq;
using RimMult.Shared.Net;
using RimMult.Shared.Packets;
using RimMult.Shared.World;

namespace RimMult.ServerCore;

/// <summary>
/// The world's chronicle: what happened (colonies founded, wars, raids, gifts…) and each player's standing. It lives
/// in <see cref="WorldState"/>, so it is kept wherever the world is: a dedicated server's world file, or the save of
/// a host playing through Steam.
/// </summary>
public sealed partial class GameServer
{
    /// <summary>Records something that happened and tells everyone.</summary>
    private void Record(ChronicleKind kind, ulong actor, ulong target = 0, int a = 0, int b = 0, int c = 0, string text = "")
    {
        var entry = new ChronicleEntry
        {
            Tick = World.Tick,
            UnixTime = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            Kind = kind,
            Actor = actor,
            ActorName = NameOfOwner(actor),
            Target = target,
            TargetName = target != 0 ? NameOfOwner(target) : "",
            A = a,
            B = b,
            C = c,
            Text = Truncate(text, 300),
        };
        World.Chronicle.Add(entry);
        if (World.Chronicle.Count > Chronicle.MaxEntries)
            World.Chronicle.RemoveRange(0, World.Chronicle.Count - Chronicle.MaxEntries);
        WorldChanged?.Invoke();
        Broadcast(PacketCodec.Encode(ChronicleUpdatePacket(full: false, new List<ChronicleEntry> { entry })), DeliveryMode.ReliableOrdered);
    }

    /// <summary>A player's stats, created on first use (with their current name).</summary>
    private PlayerStats StatsOf(ulong owner)
    {
        if (!World.Stats.TryGetValue(owner, out var stats))
        {
            stats = new PlayerStats { Owner = owner, Name = NameOfOwner(owner) };
            World.Stats[owner] = stats;
        }
        return stats;
    }

    /// <summary>The name a player goes by: connected, or as their colonies remember them.</summary>
    private string NameOfOwner(ulong owner) =>
        Players.FirstOrDefault(p => OwnerKey(p) == owner)?.Name
        ?? World.Colonies.FirstOrDefault(c => c.OwnerSteamId == owner)?.OwnerName
        ?? (World.Stats.TryGetValue(owner, out var stats) ? stats.Name : "?");

    private ChronicleUpdate ChronicleUpdatePacket(bool full, List<ChronicleEntry> entries) => new()
    {
        Full = full,
        Entries = entries,
        Stats = World.Stats.Values.Select(s =>
        {
            var copy = s.Copy();
            copy.ColorIndex = World.PlayerColors.TryGetValue(s.Owner, out var color) ? color : (byte)0;
            copy.Colonies = World.Colonies.Count(c => c.OwnerSteamId == s.Owner);
            return copy;
        }).OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase).ToList(),
    };

    private void BroadcastStats() =>
        Broadcast(PacketCodec.Encode(ChronicleUpdatePacket(full: false, new List<ChronicleEntry>())), DeliveryMode.ReliableOrdered);

    /// <summary>First time in this world: the player gets a line and a row in the table.</summary>
    private void NoteArrival(PlayerInfo player)
    {
        var key = OwnerKey(player);
        var known = World.Stats.TryGetValue(key, out var stats);
        stats = StatsOf(key);
        stats.Name = player.Name;
        if (!known)
            Record(ChronicleKind.PlayerArrived, key);
    }

    private void HandleColonyStats(PlayerInfo player, ColonyStatsReport report)
    {
        if (World.Definition == null || !player.InWorld)
            return;
        var stats = StatsOf(OwnerKey(player));
        stats.Name = player.Name;
        // Wealth is rounded to whole silver: the same colony reported twice is not news.
        var wealth = (float)Math.Round(report.Wealth);
        if (Math.Abs(stats.Wealth - wealth) < 0.5f && stats.Colonists == report.Colonists)
            return;
        stats.Wealth = wealth;
        stats.Colonists = report.Colonists;
        WorldChanged?.Invoke();
        BroadcastStats();
    }

    /// <summary>Colonies that appeared or disappeared in a player's new list.</summary>
    private void NoteColonyChanges(ulong owner, List<ColonyInfo> before, List<ColonyInfo> after)
    {
        var beforeTiles = new HashSet<string>(before.Select(c => c.Tile));
        var afterTiles = new HashSet<string>(after.Select(c => c.Tile));
        foreach (var colony in after.Where(c => !beforeTiles.Contains(c.Tile)))
            Record(ChronicleKind.ColonyFounded, owner, text: colony.Name);
        foreach (var colony in before.Where(c => !afterTiles.Contains(c.Tile)))
            Record(ChronicleKind.ColonyAbandoned, owner, text: colony.Name);
    }

    /// <summary>What a parcel means for the chronicle: a raid, a raid's end, help, or plain goods.</summary>
    private void NoteParcel(MailItem item, string address)
    {
        if (ParcelAddress.IsRaid(address))
        {
            if (item.Returned || !ParcelAddress.TryParseRaid(address, out _, out _, out _, out var live))
                return;
            StatsOf(item.FromOwner).RaidsLed++;
            StatsOf(item.ToOwner).RaidsSuffered++;
            Record(ChronicleKind.RaidLaunched, item.FromOwner, item.ToOwner, a: live ? 1 : 0, text: item.Summary);
        }
        else if (ParcelAddress.TryParseRaidReturn(address, out _, out _, out var survivors, out var captives))
        {
            // The defender sends the survivors back to the attacker; the summary lists the dead as "D:name".
            Record(ChronicleKind.RaidEnded, item.ToOwner, item.FromOwner, survivors, captives, DeadIn(item.Summary));
        }
        else if (ParcelAddress.IsHelp(address))
        {
            StatsOf(item.FromOwner).HelpsSent++;
            Record(ChronicleKind.HelpSent, item.FromOwner, item.ToOwner, text: item.Summary);
        }
        else if (ParcelAddress.IsHelpReturn(address))
        {
            Record(ChronicleKind.HelpEnded, item.ToOwner, item.FromOwner, c: DeadIn(item.Summary));
        }
        else if (!item.Returned)
        {
            StatsOf(item.FromOwner).ParcelsSent++;
            Record(ChronicleKind.ParcelSent, item.FromOwner, item.ToOwner, text: item.Summary);
        }
    }

    private static int DeadIn(string summary) =>
        summary.Split('\n').Count(line => line.StartsWith("D:", StringComparison.Ordinal));

    private static ChronicleKind? KindOf(DiplomacyEvent happened) => happened switch
    {
        DiplomacyEvent.WarDeclared => ChronicleKind.WarDeclared,
        DiplomacyEvent.PeaceMade => ChronicleKind.PeaceMade,
        DiplomacyEvent.AllianceMade => ChronicleKind.AllianceMade,
        DiplomacyEvent.AllianceBroken => ChronicleKind.AllianceBroken,
        _ => null,
    };
}
