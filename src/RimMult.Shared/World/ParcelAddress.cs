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

    /// <summary>Raiders (a war party) sent to attack the colony on <paramref name="tile"/>.</summary>
    public static string ForRaid(string raidId, RaidArrival arrival, string tile) =>
        $"{RaidPrefix}{raidId}|{(arrival == RaidArrival.DropPods ? "p" : "c")}|{tile}";

    public static bool IsRaid(string address) => address.StartsWith(RaidPrefix);

    public static bool TryParseRaid(string address, out string raidId, out RaidArrival arrival, out string tile)
    {
        raidId = tile = "";
        arrival = RaidArrival.WalkIn;
        if (!IsRaid(address))
            return false;
        var parts = address.Substring(RaidPrefix.Length).Split('|');
        if (parts.Length != 3)
            return false;
        raidId = parts[0];
        arrival = parts[1] == "p" ? RaidArrival.DropPods : RaidArrival.WalkIn;
        tile = parts[2];
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
}
