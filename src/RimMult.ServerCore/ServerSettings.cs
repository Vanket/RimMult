using System.Collections.Generic;
using RimMult.Shared.Time;

namespace RimMult.ServerCore;

public sealed class ServerSettings
{
    public string Name { get; set; } = "RimMult server";

    public int MaxPlayers { get; set; } = 10;

    /// <summary>Null or empty: no password.</summary>
    public string? Password { get; set; }

    /// <summary>
    /// Required RimWorld version string (e.g. "1.6.4633 rev1261"). Null: the first player to join defines it.
    /// </summary>
    public string? GameVersion { get; set; }

    /// <summary>
    /// Required mod list hash (see the in-game RimMult settings). Null: the first player to join defines it,
    /// which also lets the server show joining players exactly which mods differ.
    /// </summary>
    public string? ModListHash { get; set; }

    /// <summary>How often the shared clock is broadcast. 20 Hz is plenty: clients may run ahead up to the drift limit.</summary>
    public double TickGrantIntervalSeconds { get; set; } = 0.05;

    public TimeSettings Time { get; set; } = new();

    /// <summary>
    /// Only the host may create the shared world. On for a game hosted from inside RimWorld (the host's planet is
    /// the world); off for a dedicated server, where the first player to start a colony picks it.
    /// </summary>
    public bool HostCreatesWorld { get; set; }

    /// <summary>
    /// Separate colonies, or co-op in the host's colony. Co-op needs a host that simulates the game, so it is only
    /// available when hosting from inside RimWorld; a dedicated server always runs separate colonies.
    /// </summary>
    public Shared.Coop.GameMode Mode { get; set; }

    /// <summary>Players may declare war on each other and raid each other's colonies.</summary>
    public bool AllowPvp { get; set; } = true;

    /// <summary>The world market's rules.</summary>
    public MarketSettings Market { get; set; } = new();

    /// <summary>SteamIDs that may not join.</summary>
    public List<ulong> Banned { get; set; } = new();

    /// <summary>SteamIDs that may use admin commands in chat (the host always may).</summary>
    public List<ulong> Admins { get; set; } = new();
}

public sealed class MarketSettings
{
    /// <summary>NPC factions put up lots and orders, deliver stale orders and buy cheap lots (separate colonies only).</summary>
    public bool NpcTraders { get; set; } = true;

    /// <summary>At most this many NPC lots and orders on the market at once.</summary>
    public int NpcMaxLots { get; set; } = 12;
    public int NpcMaxOrders { get; set; } = 8;

    /// <summary>Lots and orders a player may have open for free; each one beyond costs <see cref="FeePercent"/> of its price.</summary>
    public int FreeListings { get; set; } = 3;
    public int FeePercent { get; set; } = 5;
}
