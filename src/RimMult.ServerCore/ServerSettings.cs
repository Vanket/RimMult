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
}
