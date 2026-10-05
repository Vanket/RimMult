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

    private const string ResearchPrefix = "research:";
    private const string TributePrefix = "tribute:";

    /// <summary>
    /// Research notes from an ally: <paramref name="points"/> of progress on the project <paramref name="defName"/>
    /// (the parcel itself carries no items).
    /// </summary>
    public static string ForResearch(string defName, int points) => $"{ResearchPrefix}{defName}|{points}";

    public static bool IsResearch(string address) => address.StartsWith(ResearchPrefix);

    public static bool TryParseResearch(string address, out string defName, out int points)
    {
        defName = "";
        points = 0;
        if (!IsResearch(address))
            return false;
        var parts = address.Substring(ResearchPrefix.Length).Split('|');
        if (parts.Length != 2 || parts[0].Length == 0 || !int.TryParse(parts[1], out points))
            return false;
        defName = parts[0];
        return points > 0;
    }

    /// <summary>A tribute payment (<paramref name="amount"/> silver) under treaty <paramref name="treatyId"/>; lands like any parcel.</summary>
    public static string ForTribute(long treatyId, int amount) => $"{TributePrefix}{treatyId}|{amount}";

    public static bool IsTribute(string address) => address.StartsWith(TributePrefix);

    private const string MarketPrefix = "market:";

    /// <summary>What a parcel from the world's market is (it lands at any home colony).</summary>
    public enum MarketKind
    {
        /// <summary>Goods bought from a lot.</summary>
        Bought,

        /// <summary>The silver for one's lot that sold.</summary>
        Sold,

        /// <summary>One's lot, taken back.</summary>
        LotReturned,

        /// <summary>Silver back: a lot sold before the purchase went through, or an order called off.</summary>
        Refund,

        /// <summary>The goods one ordered, delivered.</summary>
        OrderDelivered,

        /// <summary>The reward for delivering someone's order.</summary>
        Reward,

        /// <summary>An order nobody delivered in time: its reward back.</summary>
        OrderExpired,
    }

    /// <param name="npcFaction">A deal with an NPC faction (its def and which one of that def): the recipient's game
    /// changes its goodwill with that faction.</param>
    private const string AdminGiftAddress = "admin:gift";
    private const string AdminResearchAddress = "admin:research";

    /// <summary>Items from an admin (made by the server only, never accepted from a player's parcel).</summary>
    public static string ForAdminGift() => AdminGiftAddress;

    /// <summary>Research projects an admin finished for the recipient (def names in the payload, one per line).</summary>
    public static string ForAdminResearch() => AdminResearchAddress;

    public static bool IsAdminGift(string address) => address == AdminGiftAddress;

    public static bool IsAdminResearch(string address) => address == AdminResearchAddress;

    public static bool IsAdmin(string address) => address.StartsWith("admin:");

    public static string ForMarket(MarketKind kind, string npcFaction = "", int npcIndex = 0) =>
        npcFaction.Length == 0 ? MarketPrefix + kind : $"{MarketPrefix}{kind}|{npcFaction}|{npcIndex}";

    public static bool IsMarket(string address) => address.StartsWith(MarketPrefix);

    public static bool TryParseMarket(string address, out MarketKind kind) => TryParseMarket(address, out kind, out _, out _);

    public static bool TryParseMarket(string address, out MarketKind kind, out string npcFaction, out int npcIndex)
    {
        kind = MarketKind.Bought;
        npcFaction = "";
        npcIndex = 0;
        if (!IsMarket(address))
            return false;
        var parts = address.Substring(MarketPrefix.Length).Split('|');
        if (!System.Enum.TryParse(parts[0], out kind))
            return false;
        if (parts.Length == 3 && int.TryParse(parts[2], out npcIndex))
            npcFaction = parts[1];
        return true;
    }

    public static bool TryParseTribute(string address, out long treatyId, out int amount)
    {
        treatyId = 0;
        amount = 0;
        if (!IsTribute(address))
            return false;
        var parts = address.Substring(TributePrefix.Length).Split('|');
        return parts.Length == 2 && long.TryParse(parts[0], out treatyId) && int.TryParse(parts[1], out amount) && amount >= 0;
    }
}
