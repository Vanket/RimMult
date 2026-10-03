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
}
