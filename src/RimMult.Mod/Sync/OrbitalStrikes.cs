using System.Collections.Generic;
using System.Linq;
using RimMult.Shared.World;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.Sound;

namespace RimMult.Sync;

/// <summary>
/// Royalty's orbital strikes (aerodrone strike, salvo) on an enemy player's colony. A permit is called from a titled
/// pawn on its own map, and the friend's colony has no map here: the "Orbital strike" button on their colony offers the
/// titled colonists' strike permits (free once the cooldown is over, else for favor, as in the game), and the strike
/// goes to them like a missile (at war, PvP on, they're in the world). Their game runs the very bombardment the permit
/// would, on their base.
/// </summary>
internal static class OrbitalStrikes
{
    private static Texture2D? _icon;

    private static Texture2D Icon => _icon ??=
        ContentFinder<Texture2D>.Get("UI/Commands/CallAid", reportFailure: false) ?? BaseContent.BadTex;

    /// <summary>The colonists who may call a strike, with each strike permit they hold.</summary>
    private static IEnumerable<(Pawn Pawn, FactionPermit Permit)> Strikes() =>
        PawnsFinder.AllMaps_FreeColonistsSpawned
            .Where(p => p.royalty != null)
            .SelectMany(p => p.royalty.AllFactionPermits
                .Where(f => f.Permit.Worker is RoyalTitlePermitWorker_OrbitalStrike && f.Permit.royalAid != null)
                .Select(f => (p, f)));

    public static Command? Gizmo(RemoteColony colony)
    {
        if (!ModsConfig.RoyaltyActive || Multiplayer.Session is not { AllowPvp: true } session
            || session.RelationWith(colony.OwnerSteamId) != PlayerRelation.Hostile)
            return null;
        var command = new Command_Action
        {
            defaultLabel = "RimMult.OrbitalStrike".Translate(),
            defaultDesc = "RimMult.OrbitalStrikeDesc".Translate(colony.OwnerName),
            icon = Icon,
            action = () => Find.WindowStack.Add(new FloatMenu(Options(colony))),
        };
        var allowed = PlayerRaids.CanAttack(colony);
        if (!allowed.Accepted)
            command.Disable(allowed.FailReason);
        else if (!Strikes().Any())
            command.Disable("RimMult.OrbitalStrikeNone".Translate());
        return command;
    }

    private static List<FloatMenuOption> Options(RemoteColony colony)
    {
        var options = new List<FloatMenuOption>();
        foreach (var (pawn, permit) in Strikes())
        {
            var def = permit.Permit;
            var label = $"{pawn.LabelShortCap}: {def.LabelCap}";
            if (permit.Faction.HostileTo(Faction.OfPlayer))
            {
                options.Add(new FloatMenuOption(label + " — " + "CommandCallRoyalAidFactionHostile".Translate(permit.Faction.Named("FACTION")), null));
                continue;
            }
            var elapsed = Mathf.Max(GenTicks.TicksGame - permit.LastUsedTick, 0);
            var free = permit.LastUsedTick < 0 || elapsed >= def.CooldownTicks;
            var cost = def.royalAid.favorCost;
            if (!free && pawn.royalty.GetFavor(permit.Faction) < cost)
            {
                options.Add(new FloatMenuOption(label + " — " + "RimMult.OrbitalStrikeNoFavor".Translate(cost, permit.Faction.Named("FACTION")), null));
                continue;
            }
            var price = free ? "RimMult.OrbitalStrikeFree".Translate() : "RimMult.OrbitalStrikeCost".Translate(cost);
            options.Add(new FloatMenuOption($"{label} ({price})", () => Call(colony, pawn, permit, free)));
        }
        return options;
    }

    private static void Call(RemoteColony colony, Pawn pawn, FactionPermit permit, bool free)
    {
        var def = permit.Permit;
        if (!Parcels.Send(colony.OwnerSteamId, colony.OwnerName, ParcelAddress.ForOrbitalStrike(def.defName), new List<Thing>(),
                $"{def.LabelCap} ({pawn.LabelShortCap})", quiet: true, allowEmpty: true))
            return;
        permit.Notify_Used();
        if (!free)
            pawn.royalty.TryRemoveFavor(permit.Faction, def.royalAid.favorCost);
        SoundDefOf.OrbitalStrike_Ordered.PlayOneShotOnCamera();
        Messages.Message("RimMult.OrbitalStrikeSent".Translate(def.LabelCap, colony.OwnerName), MessageTypeDefOf.NeutralEvent, historical: false);
    }

    /// <summary>A strike from an enemy: the permit's bombardment, somewhere among this colony's people.</summary>
    public static bool Deliver(ParcelRecord parcel, string permitName)
    {
        if (parcel.Returned)
        {
            Find.LetterStack.ReceiveLetter("RimMult.OrbitalStrikeLostLabel".Translate(), "RimMult.OrbitalStrikeLostText".Translate(parcel.Summary), LetterDefOf.NeutralEvent);
            return true;
        }
        var def = DefDatabase<RoyalTitlePermitDef>.GetNamedSilentFail(permitName);
        if (def?.royalAid == null)
        {
            Log.Warning($"[RimMult] {parcel.FromName} called an unknown strike '{permitName}'.");
            return true;
        }
        var map = Find.Maps.FirstOrDefault(m => m.IsPlayerHome && m.mapPawns.FreeColonistsSpawnedCount > 0) ?? Find.AnyPlayerHomeMap;
        if (map == null)
            return false; // nowhere to hit yet: it waits

        var cell = TargetCell(map);
        var bombardment = (Bombardment)GenSpawn.Spawn(ThingDefOf.Bombardment, cell, map);
        bombardment.impactAreaRadius = def.royalAid.radius;
        bombardment.explosionRadiusRange = def.royalAid.explosionRadiusRange;
        bombardment.bombIntervalTicks = def.royalAid.intervalTicks;
        bombardment.randomFireRadius = 1;
        bombardment.explosionCount = def.royalAid.explosionCount;
        bombardment.warmupTicks = def.royalAid.warmupTicks;
        Find.LetterStack.ReceiveLetter("RimMult.OrbitalStrikeIncomingLabel".Translate(parcel.FromName),
            "RimMult.OrbitalStrikeIncomingText".Translate(parcel.FromName, def.LabelCap), LetterDefOf.ThreatBig,
            new LookTargets(new TargetInfo(cell, map)));
        return true;
    }

    /// <summary>Near a colonist (the strike is aimed at the colony), never under a thick roof (it can't go through).</summary>
    private static IntVec3 TargetCell(Map map)
    {
        bool Open(IntVec3 c) => c.InBounds(map) && c.GetRoof(map) is not { isThickRoof: true };
        var colonists = map.mapPawns.FreeColonistsSpawned;
        for (var i = 0; i < 20; i++)
        {
            var root = colonists.Count > 0 ? colonists.RandomElement().Position : map.Center;
            if (CellFinder.TryFindRandomCellNear(root, map, 8, Open, out var cell))
                return cell;
        }
        return CellFinder.TryFindRandomCell(map, Open, out var any) ? any : map.Center;
    }
}
