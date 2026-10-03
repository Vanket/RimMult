using System.Collections.Generic;
using System.Linq;
using RimWorld;
using RimWorld.Planet;
using Verse;

namespace RimMult.Sync;

/// <summary>Who a parcel goes to. Saved with pods/caravans still on their way.</summary>
public sealed class ParcelTarget : IExposable
{
    public ulong OwnerSteamId;
    public string OwnerName = "";
    public string Tile = "";

    public ParcelTarget()
    {
    }

    public ParcelTarget(RemoteColony colony)
    {
        OwnerSteamId = colony.OwnerSteamId;
        OwnerName = colony.OwnerName;
        Tile = colony.Tile.ToString();
    }

    public void ExposeData()
    {
        Scribe_Values.Look(ref OwnerSteamId, "ownerSteamId");
        Scribe_Values.Look(ref OwnerName, "ownerName", "");
        Scribe_Values.Look(ref Tile, "tile", "");
    }
}

/// <summary>Transport pods launched at another player's colony: their contents arrive there by drop pod.</summary>
public sealed class TransportersArrivalAction_SendToPlayer : TransportersArrivalAction
{
    private ParcelTarget _target = new();

    public TransportersArrivalAction_SendToPlayer()
    {
    }

    public TransportersArrivalAction_SendToPlayer(RemoteColony colony)
    {
        _target = new ParcelTarget(colony);
    }

    /// <summary>Only items can be sent for now; people and animals would be lost.</summary>
    public static FloatMenuAcceptanceReport CanSend(IEnumerable<IThingHolder> pods)
    {
        var things = pods.SelectMany(p => p.GetDirectlyHeldThings()).ToList();
        if (things.Count == 0)
            return false;
        if (!things.All(ThingPackage.CanSend))
            return FloatMenuAcceptanceReport.WithFailReasonAndMessage(
                "RimMult.ParcelItemsOnlyReason".Translate(), "RimMult.ParcelItemsOnly".Translate());
        return true;
    }

    /// <summary>Nothing lands here: the contents leave this game and drop in the other player's.</summary>
    public override bool GeneratesMap => false;

    public override FloatMenuAcceptanceReport StillValid(IEnumerable<IThingHolder> pods, PlanetTile destinationTile) =>
        CanSend(pods);

    public override void Arrived(List<ActiveTransporterInfo> transporters, PlanetTile tile)
    {
        var things = transporters.SelectMany(t => t.innerContainer).Where(ThingPackage.CanSend).ToList();
        if (Parcels.Send(_target.OwnerSteamId, _target.OwnerName, _target.Tile, things))
        {
            foreach (var transporter in transporters)
                transporter.innerContainer.ClearAndDestroyContents();
        }
    }

    public override void ExposeData()
    {
        base.ExposeData();
        Scribe_Deep.Look(ref _target, "target");
        _target ??= new ParcelTarget();
    }
}

/// <summary>A caravan that reaches another player's colony hands over everything it carries (not its members).</summary>
public sealed class CaravanArrivalAction_GiveToPlayer : CaravanArrivalAction
{
    private ParcelTarget _target = new();

    public CaravanArrivalAction_GiveToPlayer()
    {
    }

    public CaravanArrivalAction_GiveToPlayer(RemoteColony colony)
    {
        _target = new ParcelTarget(colony);
    }

    public override string Label => "RimMult.CaravanGive".Translate(_target.OwnerName);

    public override string ReportString => "RimMult.CaravanGiveReport".Translate(_target.OwnerName);

    public static FloatMenuAcceptanceReport CanGive(Caravan caravan) =>
        CaravanInventoryUtility.AllInventoryItems(caravan).Any(ThingPackage.CanSend)
            ? true
            : FloatMenuAcceptanceReport.WithFailReason("RimMult.CaravanNothingToGive".Translate());

    public override FloatMenuAcceptanceReport StillValid(Caravan caravan, PlanetTile destinationTile) => CanGive(caravan);

    public override void Arrived(Caravan caravan)
    {
        var things = CaravanInventoryUtility.AllInventoryItems(caravan).Where(ThingPackage.CanSend).ToList();
        if (!Parcels.Send(_target.OwnerSteamId, _target.OwnerName, _target.Tile, things))
            return;
        foreach (var thing in things)
            thing.Destroy();
    }

    public override void ExposeData()
    {
        base.ExposeData();
        Scribe_Deep.Look(ref _target, "target");
        _target ??= new ParcelTarget();
    }
}
