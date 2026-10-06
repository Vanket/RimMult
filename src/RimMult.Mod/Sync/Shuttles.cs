using System.Collections.Generic;
using RimWorld;
using RimWorld.Planet;
using Verse;

namespace RimMult.Sync;

/// <summary>
/// Shuttles (Odyssey's, Royalty's) launched at another player's colony travel like transport pods, but the shuttle
/// itself rides in the same container as its cargo: sending, raiding or helping would have handed the shuttle over as
/// one more item (as loot, even). It is taken out first and comes back home; only the people and the cargo go on.
/// </summary>
internal static class Shuttles
{
    /// <summary>Takes the shuttles out of arriving transporters (their contents stay).</summary>
    public static List<Thing> TakeOut(List<ActiveTransporterInfo> transporters)
    {
        var shuttles = new List<Thing>();
        foreach (var transporter in transporters)
        {
            if (transporter.GetShuttle() is { } shuttle)
            {
                transporter.RemoveShuttle();
                shuttles.Add(shuttle);
            }
        }
        return shuttles;
    }

    /// <summary>The emptied shuttles land back at home.</summary>
    public static void SendHome(List<Thing> shuttles)
    {
        if (shuttles.Count == 0)
            return;
        var map = Find.AnyPlayerHomeMap;
        if (map == null)
        {
            Log.Warning("[RimMult] A shuttle came back, but there is no home map to land on.");
            return;
        }
        foreach (var shuttle in shuttles)
            GenSpawn.Spawn(shuttle, DropCellFinder.GetBestShuttleLandingSpot(map, Faction.OfPlayer), map);
        Messages.Message("RimMult.ShuttleBackHome".Translate(), new LookTargets(shuttles), MessageTypeDefOf.NeutralEvent, historical: false);
    }
}
