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

/// <summary>Transport pods launched at another player's colony: their contents (items, colonists, animals) arrive there by drop pod.</summary>
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

    public static FloatMenuAcceptanceReport CanSend(IEnumerable<IThingHolder> pods)
    {
        var things = pods.SelectMany(p => p.GetDirectlyHeldThings()).ToList();
        if (things.Count == 0)
            return false;
        foreach (var thing in things)
        {
            if (!ThingPackage.CanSend(thing, out var reason))
                return FloatMenuAcceptanceReport.WithFailReasonAndMessage(reason, reason);
        }
        return true;
    }

    /// <summary>Nothing lands here: the contents leave this game and drop in the other player's.</summary>
    public override bool GeneratesMap => false;

    public override FloatMenuAcceptanceReport StillValid(IEnumerable<IThingHolder> pods, PlanetTile destinationTile) =>
        CanSend(pods);

    public override void Arrived(List<ActiveTransporterInfo> transporters, PlanetTile tile)
    {
        var shuttles = Shuttles.TakeOut(transporters);
        try
        {
            Deliver(transporters);
        }
        finally
        {
            Shuttles.SendHome(shuttles);
        }
    }

    private void Deliver(List<ActiveTransporterInfo> transporters)
    {
        var things = transporters.SelectMany(t => t.innerContainer).Where(ThingPackage.CanSend).ToList();
        if (Parcels.Send(_target.OwnerSteamId, _target.OwnerName, _target.Tile, things))
        {
            // Passengers now live in the other game; here they simply vanish (not killed: no death, no mourning).
            TransportersArrivalActionUtility.RemovePawnsFromWorldPawns(transporters);
            foreach (var transporter in transporters)
                transporter.innerContainer.ClearAndDestroyContents(DestroyMode.Vanish);
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

/// <summary>
/// A caravan that reaches another player's colony joins it: its colonists and animals become theirs, with everything
/// they carry.
/// </summary>
public sealed class CaravanArrivalAction_JoinPlayer : CaravanArrivalAction
{
    private ParcelTarget _target = new();

    public CaravanArrivalAction_JoinPlayer()
    {
    }

    public CaravanArrivalAction_JoinPlayer(RemoteColony colony)
    {
        _target = new ParcelTarget(colony);
    }

    public override string Label => "RimMult.CaravanJoin".Translate(_target.OwnerName);

    public override string ReportString => "RimMult.CaravanJoinReport".Translate(_target.OwnerName);

    public static FloatMenuAcceptanceReport CanJoin(Caravan caravan)
    {
        foreach (var pawn in caravan.PawnsListForReading)
        {
            if (!PawnTransfer.CanSend(pawn, out var reason))
                return FloatMenuAcceptanceReport.WithFailReasonAndMessage(reason, reason);
        }
        return caravan.PawnsListForReading.Count > 0;
    }

    public override FloatMenuAcceptanceReport StillValid(Caravan caravan, PlanetTile destinationTile) => CanJoin(caravan);

    public override void Arrived(Caravan caravan)
    {
        // Everything a caravan carries sits in its members' inventories, so packing the pawns takes it all along.
        var pawns = caravan.PawnsListForReading.ToList();
        if (!Parcels.Send(_target.OwnerSteamId, _target.OwnerName, _target.Tile, pawns.Cast<Thing>().ToList()))
            return;

        foreach (var pawn in pawns)
        {
            caravan.RemovePawn(pawn);
            if (Find.WorldPawns.Contains(pawn))
                Find.WorldPawns.RemovePawn(pawn);
            pawn.Destroy(DestroyMode.Vanish);
        }
        if (!caravan.Destroyed)
            caravan.Destroy();
    }

    public override void ExposeData()
    {
        base.ExposeData();
        Scribe_Deep.Look(ref _target, "target");
        _target ??= new ParcelTarget();
    }
}

/// <summary>A caravan at another player's colony trades with them: its cargo and animals against their stockpiles.</summary>
public sealed class CaravanArrivalAction_TradeWithPlayer : CaravanArrivalAction
{
    private ParcelTarget _target = new();

    public CaravanArrivalAction_TradeWithPlayer()
    {
    }

    public CaravanArrivalAction_TradeWithPlayer(RemoteColony colony)
    {
        _target = new ParcelTarget(colony);
    }

    public override string Label => "RimMult.CaravanTrade".Translate(_target.OwnerName);

    public override string ReportString => "RimMult.CaravanTradeReport".Translate(_target.OwnerName);

    public override FloatMenuAcceptanceReport StillValid(Caravan caravan, PlanetTile destinationTile) => true;

    public override void Arrived(Caravan caravan) => Multiplayer.StartCaravanTrade(caravan, _target.OwnerSteamId, _target.OwnerName);

    public override void ExposeData()
    {
        base.ExposeData();
        Scribe_Deep.Look(ref _target, "target");
        _target ??= new ParcelTarget();
    }
}
