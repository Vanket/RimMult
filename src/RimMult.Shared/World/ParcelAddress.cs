namespace RimMult.Shared.World;

/// <summary>
/// Where a parcel lands in the recipient's game: a colony tile (the usual case), or one of their caravans
/// (goods bought by a trading caravan), with a colony tile to fall back on if that caravan is gone by then.
/// </summary>
public static class ParcelAddress
{
    private const string CaravanPrefix = "caravan:";

    public static string ForCaravan(int caravanId, string fallbackTile) => $"{CaravanPrefix}{caravanId}|{fallbackTile}";

    /// <summary>Splits an address into an optional caravan id and the colony tile.</summary>
    public static void Parse(string address, out int? caravanId, out string tile)
    {
        caravanId = null;
        tile = address;
        if (!address.StartsWith(CaravanPrefix))
            return;

        var rest = address.Substring(CaravanPrefix.Length);
        var bar = rest.IndexOf('|');
        var idText = bar >= 0 ? rest.Substring(0, bar) : rest;
        tile = bar >= 0 ? rest.Substring(bar + 1) : "";
        if (int.TryParse(idText, out var id))
            caravanId = id;
    }

    private const string RaidPrefix = "raid:";
    private const string RaidReturnPrefix = "raidret:";

    /// <summary>How raiders arrive at the defender's colony.</summary>
    public enum RaidArrival
    {
        /// <summary>A caravan walks in from the map edge.</summary>
        WalkIn,

        /// <summary>Transport pods drop near the colony.</summary>
        DropPods,
    }

    private const string LiveMark = "live";

    /// <summary>
    /// Raiders (a war party) sent to attack the colony on <paramref name="tile"/>. A <paramref name="live"/> raid is
    /// led by the attacker in person (they join the defender's game to command it); otherwise the defender's AI runs it.
    /// </summary>
    public static string ForRaid(string raidId, RaidArrival arrival, string tile, bool live = false) =>
        $"{RaidPrefix}{raidId}|{(arrival == RaidArrival.DropPods ? "p" : "c")}|{tile}{(live ? "|" + LiveMark : "")}";

    public static bool IsRaid(string address) => address.StartsWith(RaidPrefix);

    public static bool TryParseRaid(string address, out string raidId, out RaidArrival arrival, out string tile) =>
        TryParseRaid(address, out raidId, out arrival, out tile, out _);

    public static bool TryParseRaid(string address, out string raidId, out RaidArrival arrival, out string tile, out bool live)
    {
        raidId = tile = "";
        arrival = RaidArrival.WalkIn;
        live = false;
        if (!IsRaid(address))
            return false;
        var parts = address.Substring(RaidPrefix.Length).Split('|');
        if (parts.Length != 3 && parts.Length != 4)
            return false;
        raidId = parts[0];
        arrival = parts[1] == "p" ? RaidArrival.DropPods : RaidArrival.WalkIn;
        tile = parts[2];
        live = parts.Length == 4 && parts[3] == LiveMark;
        return true;
    }

    /// <summary>
    /// The survivors of a raid going home, appearing as a caravan at <paramref name="tile"/> (the colony they
    /// attacked). The parcel holds the <paramref name="survivors"/> raiders first, then <paramref name="captives"/>
    /// kidnapped pawns, then loot.
    /// </summary>
    public static string ForRaidReturn(string raidId, string tile, int survivors, int captives) =>
        $"{RaidReturnPrefix}{raidId}|{tile}|{survivors}|{captives}";

    public static bool IsRaidReturn(string address) => address.StartsWith(RaidReturnPrefix);

    public static bool TryParseRaidReturn(string address, out string raidId, out string tile, out int survivors, out int captives)
    {
        raidId = tile = "";
        survivors = captives = 0;
        if (!IsRaidReturn(address))
            return false;
        var parts = address.Substring(RaidReturnPrefix.Length).Split('|');
        if (parts.Length != 4 || !int.TryParse(parts[2], out survivors) || !int.TryParse(parts[3], out captives))
            return false;
        raidId = parts[0];
        tile = parts[1];
        return survivors >= 0 && captives >= 0;
    }

    private const string HelpPrefix = "help:";
    private const string HelpReturnPrefix = "helpret:";

    /// <summary>An ally's people coming to help (the ally leads them in person, in this player's game).</summary>
    public static string ForHelp(string helpId, RaidArrival arrival) =>
        $"{HelpPrefix}{helpId}|{(arrival == RaidArrival.DropPods ? "p" : "c")}";

    public static bool IsHelp(string address) => address.StartsWith(HelpPrefix);

    public static bool TryParseHelp(string address, out string helpId, out RaidArrival arrival)
    {
        helpId = "";
        arrival = RaidArrival.WalkIn;
        if (!IsHelp(address))
            return false;
        var parts = address.Substring(HelpPrefix.Length).Split('|');
        if (parts.Length != 2 || parts[0].Length == 0)
            return false;
        helpId = parts[0];
        arrival = parts[1] == "p" ? RaidArrival.DropPods : RaidArrival.WalkIn;
        return true;
    }

    /// <summary>The ally's people going home after helping (they land at home by pod).</summary>
    public static string ForHelpReturn(string helpId) => HelpReturnPrefix + helpId;

    public static bool IsHelpReturn(string address) => address.StartsWith(HelpReturnPrefix);
}
