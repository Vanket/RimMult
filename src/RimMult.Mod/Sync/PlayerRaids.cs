using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using RimMult.Shared.World;
using RimWorld;
using RimWorld.Planet;
using Verse;
using Verse.AI.Group;

namespace RimMult.Sync;

/// <summary>
/// An enemy player's raid on this colony, from arrival until it is over. The raiders are the attacker's real
/// colonists, fighting under this game's AI; whoever leaves the map alive (with loot or a kidnapped colonist) goes
/// back to the attacker.
/// </summary>
public sealed class RaidRecord : IExposable
{
    public string Id = "";
    public ulong AttackerOwner;
    public string AttackerName = "";

    /// <summary>The attacked colony's tile: survivors set off home from there, on the attacker's globe.</summary>
    public string ColonyTile = "";

    public int StartedTick;

    /// <summary>The attacker leads this raid in person (joining this game as a visitor); the AI only stands in.</summary>
    public bool Live;

    /// <summary>Raiders still on the map (alive or not).</summary>
    public List<Pawn> Raiders = new();

    /// <summary>Raiders who left the map alive, kept out of the game until the raid is over.</summary>
    public List<Pawn> Gone = new();

    public void ExposeData()
    {
        Scribe_Values.Look(ref Id, "id", "");
        Scribe_Values.Look(ref AttackerOwner, "attackerOwner");
        Scribe_Values.Look(ref AttackerName, "attackerName", "");
        Scribe_Values.Look(ref ColonyTile, "colonyTile", "");
        Scribe_Values.Look(ref StartedTick, "startedTick");
        Scribe_Values.Look(ref Live, "live");
        Scribe_Collections.Look(ref Raiders, "raiders", LookMode.Reference);
        Scribe_Collections.Look(ref Gone, "gone", LookMode.Deep);
        Raiders ??= new List<Pawn>();
        Gone ??= new List<Pawn>();
        if (Scribe.mode == LoadSaveMode.PostLoadInit)
        {
            Raiders.RemoveAll(p => p == null);
            Gone.RemoveAll(p => p == null);
        }
    }
}

/// <summary>
/// PvP raids between players at war. The attacker's caravan (or pods) leaves their game and arrives in the
/// defender's as a hostile raid run by the defender's game; when it is over, the survivors return to the attacker as
/// a caravan next to the colony they attacked, with what they stole and whom they kidnapped.
/// </summary>
internal static class PlayerRaids
{
    public const int CheckIntervalTicks = 250;

    /// <summary>Pods take a while to land and a caravan to walk in: no raid is over before this.</summary>
    private const int MinRaidTicks = 1200;

    // ---------- attacker ----------

    /// <summary>Whether a raid on <paramref name="colony"/> can be launched now, and if not, why.</summary>
    public static FloatMenuAcceptanceReport CanAttack(RemoteColony colony)
    {
        var session = Multiplayer.Session;
        if (session == null || !session.AllowPvp)
            return FloatMenuAcceptanceReport.WithFailReason("RimMult.RaidPvpOff".Translate());
        if (session.RelationWith(colony.OwnerSteamId) != PlayerRelation.Hostile)
            return FloatMenuAcceptanceReport.WithFailReason("RimMult.RaidNotAtWar".Translate(colony.OwnerName));
        if (!session.Players.Any(p => p.InWorld && Multiplayer.OwnerKey(p) == colony.OwnerSteamId))
            return FloatMenuAcceptanceReport.WithFailReason("RimMult.RaidOwnerAway".Translate(colony.OwnerName));
        if (!WorldSync.InWorld)
            return FloatMenuAcceptanceReport.WithFailReason("RimMult.TradeNotInWorld".Translate());
        return true;
    }

    /// <summary>Raiders must be free colonists or colony animals (the same rule as moving to another colony), at least one a fighter.</summary>
    public static FloatMenuAcceptanceReport CanSendRaiders(IReadOnlyCollection<Pawn> pawns)
    {
        foreach (var pawn in pawns)
        {
            if (!PawnTransfer.CanSend(pawn, out var reason))
                return FloatMenuAcceptanceReport.WithFailReasonAndMessage(reason, reason);
        }
        if (!pawns.Any(p => p.RaceProps.Humanlike && !p.Downed))
            return FloatMenuAcceptanceReport.WithFailReason("RimMult.RaidNoFighters".Translate());
        return true;
    }

    /// <summary>Sends the war party; the caller removes the pawns from this game once this returns true.</summary>
    public static bool Launch(ParcelTarget target, ParcelAddress.RaidArrival arrival, List<Pawn> pawns, bool live = false)
    {
        var id = Guid.NewGuid().ToString("N").Substring(0, 8);
        var address = ParcelAddress.ForRaid(id, arrival, target.Tile, live);
        var summary = string.Join(", ", pawns.Select(p => p.LabelShortCap));
        if (!Parcels.Send(target.OwnerSteamId, target.OwnerName, address, pawns.Cast<Thing>().ToList(), summary, quiet: true))
            return false;
        if (live)
            PlayerVisit.Start(target.OwnerSteamId, target.OwnerName);
        else
            Messages.Message("RimMult.RaidLaunched".Translate(target.OwnerName), MessageTypeDefOf.ThreatBig, historical: false);
        return true;
    }

    // ---------- defender ----------

    /// <summary>An enemy war party arrives: raiders enter the map and attack under a raid lord.</summary>
    public static bool BeginDefense(ParcelRecord parcel, string raidId, ParcelAddress.RaidArrival arrival, string tileText, bool live = false)
    {
        var comp = RimMultGameComp.Instance;
        Map? map = null;
        if (PlanetTile.TryParse(tileText, out var tile) && tile.Valid)
            map = Find.Maps.FirstOrDefault(m => m.IsPlayerHome && m.Tile == tile);
        map ??= Find.AnyPlayerHomeMap;
        if (comp == null || map == null)
            return false; // nowhere to attack right now; the raid waits

        List<Thing> things;
        try
        {
            things = ThingPackage.Unpack(Convert.FromBase64String(parcel.Payload), welcome: false);
        }
        catch (Exception e)
        {
            Log.Error($"[RimMult] Could not unpack the raid from {parcel.FromName}: {e}");
            return true;
        }

        var raiders = things.OfType<Pawn>().ToList();
        if (raiders.Count == 0)
            return true;
        var faction = RaidFaction();
        foreach (var raider in raiders)
            MakeRaider(raider, faction);
        // Anything that isn't a pawn rides in the first raider's pack.
        var packer = raiders.FirstOrDefault(p => p.RaceProps.Humanlike && p.inventory != null);
        foreach (var item in things.Where(t => t is not Pawn))
            packer?.inventory.innerContainer.TryAdd(item);

        if (arrival == ParcelAddress.RaidArrival.DropPods)
        {
            DropPodUtility.DropThingsNear(DropCellFinder.FindRaidDropCenterDistant(map), map, raiders.Cast<Thing>(), forbid: false);
        }
        else
        {
            if (!RCellFinder.TryFindRandomPawnEntryCell(out var entry, map, CellFinder.EdgeRoadChance_Hostile))
                entry = CellFinder.RandomEdgeCell(map);
            foreach (var raider in raiders)
                GenSpawn.Spawn(raider, CellFinder.RandomClosewalkCellNear(entry, map, 8), map);
        }

        LordMaker.MakeNewLord(faction, new LordJob_AssaultColony(faction, canKidnap: true, canTimeoutOrFlee: true, canSteal: true), map, raiders);

        comp.Raids.Add(new RaidRecord
        {
            Id = raidId,
            AttackerOwner = parcel.FromOwner,
            AttackerName = parcel.FromName,
            ColonyTile = map.Tile.ToString(),
            StartedTick = Find.TickManager.TicksGame,
            Raiders = raiders,
            Live = live,
        });

        var text = "RimMult.RaidArrivedText".Translate(parcel.FromName, raiders.Count, string.Join(", ", raiders.Select(p => p.LabelShortCap))).ToString();
        if (live)
            text += "\n\n" + "RimMult.RaidArrivedLive".Translate(parcel.FromName);
        Find.LetterStack.ReceiveLetter(
            "RimMult.RaidArrivedLabel".Translate(parcel.FromName),
            text,
            LetterDefOf.ThreatBig,
            new LookTargets(raiders));
        return true;
    }

    /// <summary>The hidden hostile faction the raiders fight for in this game (they keep their own names and gear).</summary>
    private static Faction RaidFaction() =>
        Faction.OfAncientsHostile
        ?? Find.FactionManager.RandomEnemyFaction(allowHidden: true, allowDefeated: true)
        ?? Faction.OfPirates;

    private static void MakeRaider(Pawn pawn, Faction faction)
    {
        // The faction reference was resolved by id against this game: whatever it found is wrong.
        pawn.SetFactionDirect(null);
        pawn.SetFaction(faction);
        pawn.guest?.SetGuestStatus(null);
        pawn.ownership?.UnclaimAll();
        if (pawn.drafter != null)
            pawn.drafter.Drafted = false;
        if (ModsConfig.IdeologyActive && pawn.ideo != null && faction.ideos?.PrimaryIdeo is { } ideo)
            pawn.ideo.SetIdeo(ideo);
    }

    /// <summary>A raider walks off the map: kept aside (with whatever it carries) instead of vanishing into the world.</summary>
    public static bool TryCatchExit(Pawn pawn)
    {
        var comp = RimMultGameComp.Instance;
        var raid = comp?.Raids.FirstOrDefault(r => r.Raiders.Contains(pawn));
        if (raid == null || pawn.Dead || pawn.Faction == Faction.OfPlayer || pawn.IsPrisoner)
            return false;

        pawn.GetLord()?.Notify_PawnLost(pawn, PawnLostCondition.ExitedMap);
        if (pawn.Spawned)
            pawn.DeSpawn(DestroyMode.Vanish);
        raid.Raiders.Remove(pawn);
        raid.Gone.Add(pawn);
        return true;
    }

    /// <summary>A raid is over when no raider on the map can still fight: dead, downed, captured, or gone.</summary>
    public static void CheckRaids(RimMultGameComp comp)
    {
        foreach (var raid in comp.Raids.ToList())
        {
            raid.Raiders.RemoveAll(p => p == null);
            if (Find.TickManager.TicksGame - raid.StartedTick < MinRaidTicks)
                continue;
            // Still in a falling pod counts as on the map.
            var stillFighting = raid.Raiders.Any(p => p.SpawnedOrAnyParentSpawned && !p.Dead && !p.Downed && !p.IsPrisoner && p.Faction != Faction.OfPlayer);
            if (stillFighting)
                continue;
            comp.Raids.Remove(raid);
            try
            {
                Finish(raid);
            }
            catch (Exception e)
            {
                Log.Error($"[RimMult] Could not finish the raid by {raid.AttackerName}: {e}");
            }
        }
    }

    private static void Finish(RaidRecord raid)
    {
        var survivors = raid.Gone.ToList();
        var captives = new List<Pawn>();
        var loot = new List<Thing>();
        foreach (var survivor in survivors)
        {
            var carried = survivor.carryTracker?.innerContainer;
            if (carried == null)
                continue;
            foreach (var thing in carried.ToList())
            {
                var taken = carried.Take(thing);
                if (taken is Pawn captive)
                    captives.Add(captive);
                else if (taken != null)
                    loot.Add(taken);
            }
        }

        var dead = raid.Raiders.Where(p => p.Dead).ToList();
        var left = raid.Raiders.Where(p => !p.Dead).ToList();

        // The attacker formats the letter in their own language: names go as tagged lines.
        var report = string.Join("\n",
            dead.Select(p => "D:" + p.LabelShortCap)
                .Concat(left.Select(p => "L:" + p.LabelShortCap)));
        if (report.Length > 900)
            report = report.Substring(0, 900);

        var going = survivors.Cast<Thing>().Concat(captives.Cast<Thing>()).Concat(loot).ToList();
        var address = ParcelAddress.ForRaidReturn(raid.Id, raid.ColonyTile, survivors.Count, captives.Count);
        if (Parcels.Send(raid.AttackerOwner, raid.AttackerName, address, going, report, quiet: true, allowEmpty: true))
        {
            foreach (var thing in going)
            {
                if (thing is Pawn pawn && Find.WorldPawns.Contains(pawn))
                    Find.WorldPawns.RemovePawn(pawn);
                if (!thing.Destroyed)
                    thing.Destroy(DestroyMode.Vanish);
            }
        }

        var text = "RimMult.RaidOverText".Translate(raid.AttackerName, dead.Count, left.Count, survivors.Count);
        if (captives.Count > 0)
            text += "\n\n" + "RimMult.RaidOverKidnapped".Translate(string.Join(", ", captives.Select(p => p.LabelShortCap)));
        if (loot.Count > 0)
            text += "\n\n" + "RimMult.RaidOverStolen".Translate(ThingPackage.Summarize(loot));
        Find.LetterStack.ReceiveLetter(
            "RimMult.RaidOverLabel".Translate(raid.AttackerName),
            text,
            captives.Count > 0 ? LetterDefOf.NegativeEvent : LetterDefOf.PositiveEvent,
            new LookTargets(left));
    }

    // ---------- attacker: coming home ----------

    /// <summary>Survivors (with captives and loot) appear as a caravan next to the colony they attacked.</summary>
    public static bool DeliverReturn(ParcelRecord parcel, string tileText, int survivorCount, int captiveCount)
    {
        List<Thing> things;
        try
        {
            things = ThingPackage.Unpack(Convert.FromBase64String(parcel.Payload), welcome: false);
        }
        catch (Exception e)
        {
            Log.Error($"[RimMult] Could not unpack the raiders coming back from {parcel.FromName}: {e}");
            return true;
        }

        var pawns = things.OfType<Pawn>().ToList();
        var survivors = pawns.Take(survivorCount).ToList();
        var captives = pawns.Skip(survivorCount).Take(captiveCount).ToList();
        var loot = things.Where(t => t is not Pawn).ToList();
        foreach (var survivor in survivors)
            PawnTransfer.WelcomeArrived(survivor);
        foreach (var captive in captives)
            MakeCaptive(captive);

        var goingHome = survivors.Concat(captives).ToList();
        LookTargets? look = null;
        if (goingHome.Count > 0)
        {
            if (PlanetTile.TryParse(tileText, out var tile) && tile.Valid && VehicleCompat.WaitingAt(tile) is { } waiting)
            {
                // Their vehicles waited by the colony: the survivors (and what they took) get back in.
                foreach (var pawn in goingHome)
                {
                    if (!Find.WorldPawns.Contains(pawn))
                        Find.WorldPawns.PassToWorld(pawn);
                    waiting.AddPawn(pawn, addCarriedPawnToWorldPawnsIfAny: true);
                }
                foreach (var item in loot)
                    waiting.AddPawnOrItem(item, addCarriedPawnToWorldPawnsIfAny: true);
                look = new LookTargets(waiting);
            }
            else if (PlanetTile.TryParse(tileText, out tile) && tile.Valid)
            {
                var caravan = CaravanMaker.MakeCaravan(goingHome, Faction.OfPlayer, tile, addToWorldPawnsIfNotAlready: true);
                foreach (var item in loot)
                    caravan.AddPawnOrItem(item, addCarriedPawnToWorldPawnsIfAny: true);
                look = new LookTargets(caravan);
            }
            else if (Find.AnyPlayerHomeMap is { } home)
            {
                var spot = DropCellFinder.TradeDropSpot(home);
                DropPodUtility.DropThingsNear(spot, home, goingHome.Cast<Thing>().Concat(loot), forbid: false);
                look = new LookTargets(new TargetInfo(spot, home));
            }
            else
            {
                return false; // nowhere to put them yet
            }
        }

        var lines = parcel.Summary.Split('\n');
        var dead = lines.Where(l => l.StartsWith("D:")).Select(l => l.Substring(2)).ToList();
        var left = lines.Where(l => l.StartsWith("L:")).Select(l => l.Substring(2)).ToList();
        var none = "RimMult.RaidNobody".Translate().ToString();
        var text = "RimMult.RaidReturnText".Translate(
            parcel.FromName,
            survivors.Count > 0 ? string.Join(", ", survivors.Select(p => p.LabelShortCap)) : none,
            dead.Count > 0 ? string.Join(", ", dead) : none,
            left.Count > 0 ? string.Join(", ", left) : none);
        if (captives.Count > 0)
            text += "\n\n" + "RimMult.RaidReturnCaptives".Translate(string.Join(", ", captives.Select(p => p.LabelShortCap)));
        if (loot.Count > 0)
            text += "\n\n" + "RimMult.RaidReturnLoot".Translate(ThingPackage.Summarize(loot));
        Find.LetterStack.ReceiveLetter(
            "RimMult.RaidReturnLabel".Translate(parcel.FromName),
            text,
            survivors.Count > 0 ? LetterDefOf.PositiveEvent : LetterDefOf.NegativeEvent,
            look ?? LookTargets.Invalid);
        return true;
    }

    /// <summary>A colonist kidnapped from the enemy: this colony's prisoner, belonging to no faction here.</summary>
    private static void MakeCaptive(Pawn pawn)
    {
        pawn.SetFactionDirect(null);
        pawn.ownership?.UnclaimAll();
        pawn.guest?.SetGuestStatus(Faction.OfPlayer, GuestStatus.Prisoner);
    }
}

/// <summary>Raiders leaving the map go back to their owner instead of into this game's world.</summary>
[HarmonyPatch(typeof(Pawn), nameof(Pawn.ExitMap))]
internal static class RaiderExitPatch
{
    private static bool Prefix(Pawn __instance) => !PlayerRaids.TryCatchExit(__instance);
}

/// <summary>A caravan at an enemy player's colony attacks it.</summary>
public sealed class CaravanArrivalAction_RaidPlayer : CaravanArrivalAction
{
    private ParcelTarget _target = new();

    /// <summary>Lead the raid in person (join the defender's game) instead of leaving it to the defender's AI.</summary>
    private bool _live;

    public CaravanArrivalAction_RaidPlayer()
    {
    }

    public CaravanArrivalAction_RaidPlayer(RemoteColony colony, bool live)
    {
        _target = new ParcelTarget(colony);
        _live = live;
    }

    public override string Label => (_live ? "RimMult.RaidAttackLive" : "RimMult.RaidAttack").Translate(_target.OwnerName);

    public override string ReportString => "RimMult.RaidAttackReport".Translate(_target.OwnerName);

    /// <summary>A caravan with vehicles attacks with its crew; the vehicles stay by the colony and wait for them.</summary>
    public static FloatMenuAcceptanceReport CanAttack(Caravan caravan, RemoteColony colony)
    {
        var allowed = PlayerRaids.CanAttack(colony);
        return !allowed.Accepted ? allowed : PlayerRaids.CanSendRaiders(VehicleCompat.Crew(caravan));
    }

    public override FloatMenuAcceptanceReport StillValid(Caravan caravan, PlanetTile destinationTile) =>
        PlayerRaids.CanSendRaiders(VehicleCompat.Crew(caravan));

    public override void Arrived(Caravan caravan)
    {
        var colony = Find.WorldObjects.AllWorldObjects.OfType<RemoteColony>().FirstOrDefault(c => c.OwnerSteamId == _target.OwnerSteamId);
        var allowed = colony != null ? PlayerRaids.CanAttack(colony) : FloatMenuAcceptanceReport.WithFailReason("RimMult.RaidOwnerAway".Translate(_target.OwnerName));
        if (!allowed.Accepted)
        {
            Messages.Message("RimMult.RaidCancelled".Translate(_target.OwnerName, allowed.FailReason), caravan, MessageTypeDefOf.RejectInput, historical: false);
            return;
        }

        // Everything a caravan carries sits in its members' inventories, so packing the pawns takes it all along.
        // Vehicles never leave this game: their crew gets out and attacks, the vehicles (and their cargo) wait here.
        var pawns = VehicleCompat.Crew(caravan);
        var vehicles = VehicleCompat.HasVehicles(caravan);
        if (!PlayerRaids.Launch(_target, ParcelAddress.RaidArrival.WalkIn, pawns, _live))
            return;
        foreach (var pawn in pawns)
        {
            VehicleCompat.TakeOut(caravan, pawn);
            if (Find.WorldPawns.Contains(pawn))
                Find.WorldPawns.RemovePawn(pawn);
            pawn.Destroy(DestroyMode.Vanish);
        }
        if (vehicles && !caravan.Destroyed && caravan.PawnsListForReading.Count > 0)
        {
            Messages.Message("RimMult.RaidVehiclesWait".Translate(_target.OwnerName), caravan, MessageTypeDefOf.NeutralEvent, historical: false);
            return;
        }
        if (!caravan.Destroyed)
            caravan.Destroy();
    }

    public override void ExposeData()
    {
        base.ExposeData();
        Scribe_Deep.Look(ref _target, "target");
        Scribe_Values.Look(ref _live, "live");
        _target ??= new ParcelTarget();
    }
}

/// <summary>Transport pods launched at an enemy player's colony: the passengers land as a drop pod raid.</summary>
public sealed class TransportersArrivalAction_RaidPlayer : TransportersArrivalAction
{
    private ParcelTarget _target = new();
    private bool _live;

    public TransportersArrivalAction_RaidPlayer()
    {
    }

    public TransportersArrivalAction_RaidPlayer(RemoteColony colony, bool live)
    {
        _target = new ParcelTarget(colony);
        _live = live;
    }

    public static FloatMenuAcceptanceReport CanAttack(IEnumerable<IThingHolder> pods, RemoteColony colony)
    {
        var allowed = PlayerRaids.CanAttack(colony);
        if (!allowed.Accepted)
            return allowed;
        var pawns = pods.SelectMany(p => p.GetDirectlyHeldThings()).OfType<Pawn>().ToList();
        return PlayerRaids.CanSendRaiders(pawns);
    }

    public override bool GeneratesMap => false;

    public override FloatMenuAcceptanceReport StillValid(IEnumerable<IThingHolder> pods, PlanetTile destinationTile) =>
        PlayerRaids.CanSendRaiders(pods.SelectMany(p => p.GetDirectlyHeldThings()).OfType<Pawn>().ToList());

    public override void Arrived(List<ActiveTransporterInfo> transporters, PlanetTile tile)
    {
        var contents = transporters.SelectMany(t => t.innerContainer).ToList();
        var pawns = contents.OfType<Pawn>().ToList();
        var colony = Find.WorldObjects.AllWorldObjects.OfType<RemoteColony>().FirstOrDefault(c => c.OwnerSteamId == _target.OwnerSteamId);
        if (colony == null || !PlayerRaids.CanAttack(colony).Accepted || pawns.Count == 0)
        {
            // Can't attack after all (peace was made, the enemy left): the pods come back down at home.
            if (Find.AnyPlayerHomeMap is { } home)
            {
                foreach (var transporter in transporters)
                    transporter.innerContainer.TryDropAll(DropCellFinder.TradeDropSpot(home), home, ThingPlaceMode.Near);
            }
            Messages.Message("RimMult.RaidCancelled".Translate(_target.OwnerName, ""), MessageTypeDefOf.RejectInput, historical: false);
            return;
        }

        // Cargo goes along in the raiders' packs.
        var carriers = pawns.Where(p => p.RaceProps.Humanlike && p.inventory != null).ToList();
        var index = 0;
        foreach (var item in contents.Where(t => t is not Pawn).ToList())
        {
            if (carriers.Count == 0)
                break;
            var owner = transporters.First(t => t.innerContainer.Contains(item)).innerContainer;
            owner.Remove(item);
            carriers[index++ % carriers.Count].inventory.innerContainer.TryAdd(item);
        }

        if (!PlayerRaids.Launch(_target, ParcelAddress.RaidArrival.DropPods, pawns, _live))
            return;
        TransportersArrivalActionUtility.RemovePawnsFromWorldPawns(transporters);
        foreach (var transporter in transporters)
            transporter.innerContainer.ClearAndDestroyContents(DestroyMode.Vanish);
    }

    public override void ExposeData()
    {
        base.ExposeData();
        Scribe_Deep.Look(ref _target, "target");
        Scribe_Values.Look(ref _live, "live");
        _target ??= new ParcelTarget();
    }
}
