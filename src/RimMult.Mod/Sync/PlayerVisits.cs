using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using RimMult.ClientCore;
using RimMult.Coop;
using RimMult.Shared.Coop;
using RimMult.Shared.World;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;
using Verse.AI;
using Verse.AI.Group;

namespace RimMult.Sync;

/// <summary>An ally's people helping in this colony, led by the ally in person (they are this colony's colonists meanwhile).</summary>
public sealed class HelpRecord : IExposable
{
    public string Id = "";
    public ulong HelperOwner;
    public string HelperName = "";
    public List<Pawn> Helpers = new();

    /// <summary>When they arrived (real time, not saved): helpers whose leader never comes go home after a while.</summary>
    public float ArrivedAt = float.NegativeInfinity;

    public void ExposeData()
    {
        Scribe_Values.Look(ref Id, "id", "");
        Scribe_Values.Look(ref HelperOwner, "helperOwner");
        Scribe_Values.Look(ref HelperName, "helperName", "");
        Scribe_Collections.Look(ref Helpers, "helpers", LookMode.Reference);
        Helpers ??= new List<Pawn>();
        if (Scribe.mode == LoadSaveMode.PostLoadInit)
            Helpers.RemoveAll(p => p == null);
    }
}

/// <summary>
/// Visits, host's side: another player joins this game (the co-op machinery, between two players) to command their
/// own pawns here — the raiders of their live raid, or the people they sent to help. Their orders arrive as jobs;
/// between orders those pawns hold their ground (shooting what comes near). Nobody else gives them orders. When a
/// raider's leader leaves, the AI takes the raid over; when a helper's leader leaves, the helpers go home.
/// </summary>
internal static class PlayerVisits
{
    private const float ControlInterval = 0.5f;

    /// <summary>Helpers whose leader doesn't come within this long (real seconds) go home.</summary>
    private const float HelpWaitSeconds = 180f;

    /// <summary>What <see cref="CoopChannel.VisitInfo"/> starts with: the kind of visit.</summary>
    public const int RaidVisit = 0;
    public const int HelpVisit = 1;

    /// <summary>Jobs a visitor may give its raiders: moving, fighting, taking things and people, looking after themselves.</summary>
    private static readonly HashSet<string> AllowedJobs = new()
    {
        "Goto", "AttackMelee", "AttackStatic", "Wait_Combat", "Wait", "Wait_MaintainPosture", "Equip", "Wear", "RemoveApparel",
        "DropEquipment", "TakeInventory", "Kidnap", "Steal", "Ingest", "TendPatient", "Strip", "Flee", "FleeAndCower",
        "UseVerbOnThing", "UseVerbOnThingStatic", "Reload", "Open",
    };

    /// <summary>The player's way of taking someone (capture, carry off) means kidnapping for a raider.</summary>
    private static readonly HashSet<string> KidnapJobs = new() { "Capture", "Arrest", "Rescue", "CarryDownedPawn", "CarryToPrisonerBedDrafted" };

    private static readonly AccessTools.FieldRef<Job, int> JobLoadId = AccessTools.FieldRefAccess<Job, int>("loadID");

    private sealed class Visit
    {
        public RaidRecord? Raid;
        public HelpRecord? Help;
        public List<Pawn> Pawns => Raid?.Raiders ?? Help!.Helpers;
        public string Leader => Raid?.AttackerName ?? Help!.HelperName;
    }

    /// <summary>Visitor (player id) → what it leads here.</summary>
    private static readonly Dictionary<int, Visit> Visitors = new();

    private static float _lastControl;

    /// <summary>True while a visitor's order (or the hold-your-ground default) is being given: nobody else may order those pawns.</summary>
    public static bool Ordering { get; private set; }

    private static ulong? OwnerOf(ClientSession session, int playerId) =>
        session.Players.FirstOrDefault(p => p.Id == playerId) is { } player ? Multiplayer.OwnerKey(player) : null;

    private static Visit? VisitOf(ClientSession session, int playerId)
    {
        if (OwnerOf(session, playerId) is not { } owner || RimMultGameComp.Instance is not { } comp)
            return null;
        if (comp.Raids.FirstOrDefault(r => r.Live && r.AttackerOwner == owner) is { } raid)
            return new Visit { Raid = raid };
        if (comp.Helps.FirstOrDefault(h => h.HelperOwner == owner) is { } help)
            return new Visit { Help = help };
        return null;
    }

    /// <summary>A player may visit while its live raid is on here, or while its helpers are here.</summary>
    public static bool CanHost(ClientSession session, int playerId) => VisitOf(session, playerId) != null;

    /// <summary>Pawns some visitor commands (nobody else orders them).</summary>
    public static bool IsControlled(Pawn pawn)
    {
        foreach (var visit in Visitors.Values)
            if (visit.Pawns.Contains(pawn))
                return true;
        return false;
    }

    /// <summary>Raiders some visitor commands (what they are doing is not shown here).</summary>
    public static bool IsControlledRaider(Pawn pawn)
    {
        foreach (var visit in Visitors.Values)
            if (visit.Raid != null && visit.Pawns.Contains(pawn))
                return true;
        return false;
    }

    public static string? LeaderOf(Pawn pawn) => Visitors.Values.FirstOrDefault(v => v.Pawns.Contains(pawn))?.Leader;

    /// <summary>The visitor got this game: its pawns are its own from now on.</summary>
    public static void Joined(ClientSession session, int playerId)
    {
        var visit = VisitOf(session, playerId);
        if (visit == null)
            return;
        Visitors[playerId] = visit;
        foreach (var pawn in visit.Pawns.Where(p => p != null))
        {
            pawn.GetLord()?.RemovePawn(pawn);
            if (pawn.Spawned && !pawn.Dead)
                pawn.jobs?.EndCurrentJob(JobCondition.InterruptForced);
        }
        var info = new List<int> { visit.Help != null ? HelpVisit : RaidVisit };
        info.AddRange(visit.Pawns.Where(p => p != null).Select(p => p.thingIDNumber));
        session.SendCoop(CoopChannel.VisitInfo, CoopIds.Encode(info), playerId);
        Messages.Message((visit.Help != null ? "RimMult.HelpJoining" : "RimMult.VisitJoining").Translate(visit.Leader), MessageTypeDefOf.NeutralEvent, historical: false);
    }

    /// <summary>The visitor left (went home, lost connection): raiders fight on under the AI, helpers go home.</summary>
    public static void Left(int playerId)
    {
        if (!Visitors.TryGetValue(playerId, out var visit))
            return;
        Visitors.Remove(playerId);
        var comp = RimMultGameComp.Instance;
        if (visit.Help != null)
        {
            if (comp?.Helps.Contains(visit.Help) == true)
                SendHelpersHome(comp, visit.Help);
            return;
        }
        if (comp?.Raids.Contains(visit.Raid!) != true)
            return;
        var fighters = visit.Pawns.Where(p => p is { Spawned: true, Dead: false, Downed: false } && p.GetLord() == null).ToList();
        if (fighters.Count > 0)
        {
            var faction = fighters[0].Faction;
            foreach (var pawn in fighters)
                pawn.jobs?.EndCurrentJob(JobCondition.InterruptForced);
            LordMaker.MakeNewLord(faction, new LordJob_AssaultColony(faction, canKidnap: true, canTimeoutOrFlee: true, canSteal: true), fighters[0].Map, fighters);
        }
        Messages.Message("RimMult.VisitLeft".Translate(visit.Leader), MessageTypeDefOf.NeutralEvent, historical: false);
    }

    /// <summary>Keeps idle visitor pawns holding their ground, ends visits whose raid is over, sends unled helpers home.</summary>
    public static void Update(ClientSession session)
    {
        var comp = RimMultGameComp.Instance;
        if ((Visitors.Count == 0 && (comp == null || comp.Helps.Count == 0)) || Current.ProgramState != ProgramState.Playing)
            return;
        var now = Time.realtimeSinceStartup;
        if (now - _lastControl < ControlInterval)
            return;
        _lastControl = now;

        foreach (var pair in Visitors.ToList())
        {
            var over = pair.Value.Raid != null ? comp?.Raids.Contains(pair.Value.Raid) != true : comp?.Helps.Contains(pair.Value.Help!) != true;
            if (over)
            {
                // The raid is over (its survivors are on their way back): the visit ends with it.
                Visitors.Remove(pair.Key);
                CoopHost.DropGuest(pair.Key);
                session.SendCoop(CoopChannel.VisitEnd, Array.Empty<byte>(), pair.Key);
                continue;
            }
            foreach (var pawn in pair.Value.Pawns)
            {
                if (pawn is not { Spawned: true, Dead: false, Downed: false } || pawn.InMentalState || pawn.jobs == null)
                    continue;
                if (pawn.CurJob == null || !pawn.CurJob.playerForced)
                    Order(pawn, JobMaker.MakeJob(JobDefOf.Wait_Combat), queue: false);
            }
        }

        if (comp == null)
            return;
        foreach (var help in comp.Helps.ToList())
        {
            if (float.IsNegativeInfinity(help.ArrivedAt))
                help.ArrivedAt = now; // loaded from a save: the wait starts now
            var led = Visitors.Values.Any(v => v.Help == help);
            if (!led && now - help.ArrivedAt > HelpWaitSeconds)
                SendHelpersHome(comp, help);
        }
    }

    private static void Order(Pawn pawn, Job job, bool queue)
    {
        Ordering = true;
        try
        {
            pawn.jobs.TryTakeOrderedJob(job, JobTag.Misc, queue);
        }
        finally
        {
            Ordering = false;
        }
    }

    /// <summary>An order from a visitor: a job for one of its pawns (anything else is refused).</summary>
    public static void Execute(int playerId, byte[] data)
    {
        CoopCommand command;
        try
        {
            command = CoopCommand.Decode(data);
        }
        catch (Exception)
        {
            return;
        }
        if (command.Kind != CoopCommandKind.Job || command.ThingIds.Count == 0 || !Visitors.TryGetValue(playerId, out var visit))
            return;
        var pawn = visit.Pawns.FirstOrDefault(p => p != null && p.thingIDNumber == command.ThingIds[0]);
        if (pawn is not { Spawned: true, Dead: false } || pawn.jobs == null)
            return;

        Job? job = null;
        try
        {
            ScribeMemory.Load(ScribeMemory.Parse(command.Extra), new HashSet<string>(), () => Scribe_Deep.Look(ref job, "job"));
        }
        catch (Exception e)
        {
            Log.Warning($"[RimMult] Visit: could not read an order: {e.Message}");
            return;
        }
        if (job?.def == null)
            return;

        // Helpers are this colony's colonists for now: anything a colonist may do. Raiders only fight and take.
        if (visit.Raid != null)
        {
            if (KidnapJobs.Contains(job.def.defName) && job.targetA.Thing is Pawn victim)
                job = JobMaker.MakeJob(JobDefOf.Kidnap, victim);
            // Abilities (vanilla ones carry job.ability; mods' psycasts have their own cast jobs) are fair in a fight.
            var casting = job.ability != null || job.def.defName.IndexOf("Cast", StringComparison.Ordinal) >= 0
                                              || job.def.defName.IndexOf("Ability", StringComparison.Ordinal) >= 0;
            if (!casting && !AllowedJobs.Contains(job.def.defName))
            {
                Log.Message($"[RimMult] Visit: order {job.def.defName} is not for raiders, ignored.");
                return;
            }
            // Carrying someone or something off needs a way out: the nearest edge.
            if ((job.def == JobDefOf.Kidnap || job.def == JobDefOf.Steal) && !job.targetB.IsValid && RCellFinder.TryFindBestExitSpot(pawn, out var exit))
                job.targetB = exit;
            if (job.def == JobDefOf.Kidnap || job.def == JobDefOf.Steal)
                job.count = Math.Max(1, job.targetA.Thing?.stackCount ?? 1);
        }

        JobLoadId(job) = Find.UniqueIDsManager.GetNextJobID();
        job.playerForced = true;
        Order(pawn, job, command.Queue);
    }

    // ---------- helpers ----------

    /// <summary>An ally's people arrive to help: on the map this player is looking at (a quest site, under attack), or at home.</summary>
    public static bool BeginHelp(ParcelRecord parcel, string helpId, ParcelAddress.RaidArrival arrival)
    {
        var comp = RimMultGameComp.Instance;
        var map = Find.CurrentMap is { } current && (current.IsPlayerHome || current.mapPawns.FreeColonistsSpawnedCount > 0)
            ? current
            : Find.AnyPlayerHomeMap;
        if (comp == null || map == null)
            return false; // nowhere to arrive right now; they wait

        List<Thing> things;
        try
        {
            things = ThingPackage.Unpack(Convert.FromBase64String(parcel.Payload), welcome: false);
        }
        catch (Exception e)
        {
            Log.Error($"[RimMult] Could not unpack the help from {parcel.FromName}: {e}");
            return true;
        }
        var helpers = things.OfType<Pawn>().ToList();
        if (helpers.Count == 0)
            return true;
        foreach (var helper in helpers)
            PawnTransfer.WelcomeArrived(helper);
        var packer = helpers.FirstOrDefault(p => p.RaceProps.Humanlike && p.inventory != null);
        foreach (var item in things.Where(t => t is not Pawn))
            packer?.inventory.innerContainer.TryAdd(item);

        var near = map.mapPawns.FreeColonistsSpawned.FirstOrDefault(p => !helpers.Contains(p))?.Position ?? DropCellFinder.TradeDropSpot(map);
        if (arrival == ParcelAddress.RaidArrival.DropPods)
        {
            DropPodUtility.DropThingsNear(near, map, helpers.Cast<Thing>(), forbid: false);
        }
        else
        {
            if (!RCellFinder.TryFindRandomPawnEntryCell(out var entry, map, CellFinder.EdgeRoadChance_Friendly))
                entry = CellFinder.RandomEdgeCell(map);
            foreach (var helper in helpers)
                GenSpawn.Spawn(helper, CellFinder.RandomClosewalkCellNear(entry, map, 8), map);
        }

        comp.Helps.Add(new HelpRecord
        {
            Id = helpId,
            HelperOwner = parcel.FromOwner,
            HelperName = parcel.FromName,
            Helpers = helpers,
            ArrivedAt = Time.realtimeSinceStartup,
        });
        Find.LetterStack.ReceiveLetter(
            "RimMult.HelpArrivedLabel".Translate(parcel.FromName),
            "RimMult.HelpArrivedText".Translate(parcel.FromName, string.Join(", ", helpers.Select(p => p.LabelShortCap))),
            LetterDefOf.PositiveEvent,
            new LookTargets(helpers));
        return true;
    }

    /// <summary>The helpers (alive, wounded or not, with what they carry) go back to their owner.</summary>
    private static void SendHelpersHome(RimMultGameComp comp, HelpRecord help)
    {
        comp.Helps.Remove(help);
        var going = help.Helpers.Where(p => p != null && !p.Dead && !p.Destroyed).ToList();
        var dead = help.Helpers.Where(p => p != null && p.Dead).ToList();
        foreach (var pawn in going)
        {
            if (pawn.Spawned)
                pawn.DeSpawn(DestroyMode.Vanish);
            PawnTransfer.PrepareToLeave(pawn);
        }
        var report = string.Join("\n", dead.Select(p => "D:" + p.LabelShortCap));
        if (Parcels.Send(help.HelperOwner, help.HelperName, ParcelAddress.ForHelpReturn(help.Id), going.Cast<Thing>().ToList(), report, quiet: true, allowEmpty: true))
        {
            foreach (var pawn in going)
            {
                if (Find.WorldPawns.Contains(pawn))
                    Find.WorldPawns.RemovePawn(pawn);
                if (!pawn.Destroyed)
                    pawn.Destroy(DestroyMode.Vanish);
            }
        }
        Messages.Message("RimMult.HelpLeft".Translate(help.HelperName), MessageTypeDefOf.NeutralEvent, historical: false);
    }

    /// <summary>Our people are back from helping an ally: they land at home.</summary>
    public static bool DeliverHelpReturn(ParcelRecord parcel)
    {
        var home = Find.AnyPlayerHomeMap;
        if (home == null)
            return false;
        List<Thing> things;
        try
        {
            things = ThingPackage.Unpack(Convert.FromBase64String(parcel.Payload), welcome: false);
        }
        catch (Exception e)
        {
            Log.Error($"[RimMult] Could not unpack the people coming back from {parcel.FromName}: {e}");
            return true;
        }
        foreach (var pawn in things.OfType<Pawn>())
            PawnTransfer.WelcomeArrived(pawn);
        var spot = DropCellFinder.TradeDropSpot(home);
        if (things.Count > 0)
            DropPodUtility.DropThingsNear(spot, home, things, forbid: false);

        var none = "RimMult.RaidNobody".Translate().ToString();
        var back = things.OfType<Pawn>().Select(p => p.LabelShortCap).ToList();
        var dead = parcel.Summary.Split('\n').Where(l => l.StartsWith("D:")).Select(l => l.Substring(2)).ToList();
        Find.LetterStack.ReceiveLetter(
            "RimMult.HelpBackLabel".Translate(parcel.FromName),
            "RimMult.HelpBackText".Translate(parcel.FromName, back.Count > 0 ? string.Join(", ", back) : none, dead.Count > 0 ? string.Join(", ", dead) : none),
            LetterDefOf.NeutralEvent,
            new LookTargets(new TargetInfo(spot, home)));
        return true;
    }

    /// <summary>Whether this colony's people can go and help that ally now.</summary>
    public static FloatMenuAcceptanceReport CanHelp(RemoteColony colony, IReadOnlyCollection<Pawn> pawns)
    {
        var session = Multiplayer.Session;
        if (session == null || session.RelationWith(colony.OwnerSteamId) != Shared.World.PlayerRelation.Allied)
            return FloatMenuAcceptanceReport.WithFailReason("RimMult.HelpNotAllied".Translate(colony.OwnerName));
        if (!session.Players.Any(p => p.InWorld && Multiplayer.OwnerKey(p) == colony.OwnerSteamId))
            return FloatMenuAcceptanceReport.WithFailReason("RimMult.RaidOwnerAway".Translate(colony.OwnerName));
        if (!WorldSync.InWorld)
            return FloatMenuAcceptanceReport.WithFailReason("RimMult.TradeNotInWorld".Translate());
        foreach (var pawn in pawns)
            if (!PawnTransfer.CanSend(pawn, out var reason))
                return FloatMenuAcceptanceReport.WithFailReasonAndMessage(reason, reason);
        if (!pawns.Any(p => p.RaceProps.Humanlike && !p.Downed))
            return FloatMenuAcceptanceReport.WithFailReason("RimMult.RaidNoFighters".Translate());
        return true;
    }

    /// <summary>Sends the helpers off and starts the trip; the caller removes the pawns from this game once this returns true.</summary>
    public static bool LaunchHelp(ParcelTarget target, ParcelAddress.RaidArrival arrival, List<Pawn> pawns)
    {
        var id = Guid.NewGuid().ToString("N").Substring(0, 8);
        var summary = string.Join(", ", pawns.Select(p => p.LabelShortCap));
        if (!Parcels.Send(target.OwnerSteamId, target.OwnerName, ParcelAddress.ForHelp(id, arrival), pawns.Cast<Thing>().ToList(), summary, quiet: true))
            return false;
        PlayerVisit.Start(target.OwnerSteamId, target.OwnerName);
        return true;
    }
}

/// <summary>
/// Live raids, attacker's side: the trip into the defender's game. The home game is saved, the defender's game is
/// loaded as a visitor's copy (the raiders are this player's colonists there, the defender's colony the enemy),
/// orders to the raiders go to the defender as jobs, and when the raid ends (or this player leaves) the home game
/// is loaded again — the survivors come back as usual, as a caravan by the colony they attacked.
/// </summary>
internal static class PlayerVisit
{
    public const string HomeSave = "RimMult_VisitHome";

    private static readonly AccessTools.FieldRef<Pawn_JobTracker, Pawn> TrackerPawn = AccessTools.FieldRefAccess<Pawn_JobTracker, Pawn>("pawn");

    /// <summary>A visit to start once the current tick is over: (defender's owner key, name).</summary>
    private static (ulong Owner, string Name)? _pending;

    /// <summary>The copy being left: until a different game is playing, this player isn't home yet.</summary>
    private static Game? _leaving;

    /// <summary>Away from home: from saving the home game until it is loaded again. The world must not see this player's copy.</summary>
    public static bool Travelling { get; private set; }

    /// <summary>Called when the raid has been sent: the trip starts on the next frame (not in the middle of a tick).</summary>
    public static void Start(ulong defenderOwner, string defenderName) => _pending = (defenderOwner, defenderName);

    public static void Update(ClientSession? session)
    {
        if (_leaving != null && Current.ProgramState == ProgramState.Playing && Current.Game != _leaving && !LongEventHandler.AnyEventNowOrWaiting)
        {
            _leaving = null;
            Travelling = false;
            Messages.Message("RimMult.VisitBackHome".Translate(), MessageTypeDefOf.PositiveEvent, historical: false);
        }
        else if (_leaving != null && Current.ProgramState == ProgramState.Entry && !LongEventHandler.AnyEventNowOrWaiting)
        {
            // The home save couldn't be loaded: the player is in the main menu and can load a save themselves.
            _leaving = null;
            Travelling = false;
        }

        if (_pending is not { } pending || session is not { State: ClientState.Connected } || LongEventHandler.AnyEventNowOrWaiting || !ScribeMemory.Idle)
            return;
        _pending = null;
        var defender = session.Players.FirstOrDefault(p => Multiplayer.OwnerKey(p) == pending.Owner && p.InWorld);
        if (defender == null)
        {
            Messages.Message("RimMult.RaidOwnerAway".Translate(pending.Name), MessageTypeDefOf.RejectInput, historical: false);
            return;
        }

        // The war party itself must be on its way before this player leaves the world.
        if (WorldSync.InWorld)
            Parcels.Update(session);
        Travelling = true;
        WorldSync.LeaveNow(session);
        try
        {
            GameDataSaveLoader.SaveGame(HomeSave);
        }
        catch (Exception e)
        {
            Log.Error($"[RimMult] Could not save the home colony before the raid: {e}");
            Travelling = false;
            return;
        }
        Messages.Message("RimMult.VisitGoing".Translate(pending.Name), MessageTypeDefOf.NeutralEvent, historical: false);
        CoopGuest.RequestVisit(session, defender.Id);
    }

    public static bool IsMine(Pawn pawn) => CoopGuest.Visiting && CoopGuest.VisitPawns.Contains(pawn.thingIDNumber);

    /// <summary>The colony this copy belongs to plays the enemy here.</summary>
    private static Faction? Enemy() =>
        Faction.OfAncientsHostile ?? Find.FactionManager.RandomEnemyFaction(allowHidden: true, allowDefeated: true);

    /// <summary>
    /// The visited game is loaded. For a raid, the raiders become this player's pawns and the defender's colony the
    /// enemy; helpers already are colonists there (of the ally's colony, which stays friendly).
    /// </summary>
    public static void CopyLoaded()
    {
        var enemy = CoopGuest.VisitHelp ? null : Enemy();
        foreach (var map in Find.Maps)
        {
            foreach (var thing in map.listerThings.AllThings.ToList())
            {
                if (!Adjust(thing, enemy))
                    continue;
                if (thing is Pawn pawn && pawn.Spawned)
                    map.mapPawns.UpdateRegistryForPawn(pawn);
            }
        }
        Find.ColonistBar?.MarkColonistsDirty();
        var mine = Find.Maps.SelectMany(m => m.mapPawns.AllPawnsSpawned).Where(IsMine).ToList();
        if (mine.Count > 0)
        {
            Find.Selector.ClearSelection();
            foreach (var pawn in mine)
                Find.Selector.Select(pawn, playSound: false, forceDesignatorDeselect: false);
            Find.CameraDriver.JumpToCurrentMapLoc(mine[0].Position);
        }
        if (CoopGuest.VisitHelp)
            Find.LetterStack.ReceiveLetter("RimMult.HelpStartedLabel".Translate(), "RimMult.HelpStartedText".Translate(), LetterDefOf.NeutralEvent, new LookTargets(mine));
        else
            Find.LetterStack.ReceiveLetter("RimMult.VisitStartedLabel".Translate(), "RimMult.VisitStartedText".Translate(), LetterDefOf.NeutralEvent, new LookTargets(mine));
    }

    /// <summary>A thing the defender's game just sent, before it spawns: the same swap of sides.</summary>
    public static void AdjustLoaded(Thing thing) => Adjust(thing, CoopGuest.VisitHelp ? null : Enemy());

    private static bool Adjust(Thing thing, Faction? enemy)
    {
        if (thing is Pawn pawn && IsMine(pawn))
        {
            if (pawn.Faction == Faction.OfPlayer)
                return false;
            pawn.SetFactionDirect(Faction.OfPlayer);
            pawn.GetLord()?.RemovePawn(pawn);
            // Spawned or about to be: either way it needs what a colonist has (drafting above all).
            PawnComponentsUtility.AddAndRemoveDynamicComponents(pawn, actAsIfSpawned: true);
            return true;
        }
        if (thing.Faction == Faction.OfPlayer && enemy != null)
        {
            thing.SetFactionDirect(enemy);
            return true;
        }
        return false;
    }

    /// <summary>Visitor: an order for one of the raiders goes to the defender's game.</summary>
    public static Pawn? PawnOf(Pawn_JobTracker tracker) => TrackerPawn(tracker);

    private static float _lastNotYours;

    public static bool RelayJob(Pawn_JobTracker tracker, Job job, bool queue)
    {
        var pawn = TrackerPawn(tracker);
        if (pawn == null)
            return false;
        if (!IsMine(pawn))
        {
            if (pawn.Faction == Faction.OfPlayer && Time.realtimeSinceStartup - _lastNotYours > 2f)
            {
                _lastNotYours = Time.realtimeSinceStartup;
                Messages.Message("RimMult.VisitNotYours".Translate(), pawn, MessageTypeDefOf.RejectInput, historical: false);
            }
            return false;
        }
        var saved = job;
        string xml;
        try
        {
            xml = ScribeMemory.Save(() => Scribe_Deep.Look(ref saved, "job"));
        }
        catch (Exception e)
        {
            Log.Warning($"[RimMult] Visit: could not send the order {job.def?.defName}: {e.Message}");
            return true;
        }
        CoopGuest.SendToHost(CoopChannel.Command, new CoopCommand
        {
            Kind = CoopCommandKind.Job,
            MapId = pawn.MapHeld?.uniqueID ?? -1,
            ThingIds = { pawn.thingIDNumber },
            Name = job.def?.defName ?? "",
            Extra = xml,
            Queue = queue || CoopCommands.QueueHeld(),
        }.Encode());
        return true;
    }

    /// <summary>The defender's game says the raid is over.</summary>
    public static void Over()
    {
        Messages.Message("RimMult.VisitOver".Translate(), MessageTypeDefOf.NeutralEvent, historical: false);
        ReturnHome(null);
    }

    /// <summary>Back to the home game (the raiders stay behind under the defender's AI if the raid isn't over).</summary>
    public static void ReturnHome(string? why)
    {
        if (CoopGuest.Visiting && CoopGuest.Target >= 0)
            CoopGuest.SendToHost(CoopChannel.VisitEnd, Array.Empty<byte>());
        if (CoopGuest.State != CoopGuest.Phase.None)
            CoopGuest.Reset();
        if (!why.NullOrEmpty())
            Messages.Message("RimMult.VisitInterrupted".Translate(why!), MessageTypeDefOf.RejectInput, historical: false);

        if (!File.Exists(GenFilePaths.FilePathForSavedGame(HomeSave)))
        {
            Travelling = false;
            if (Current.ProgramState == ProgramState.Playing)
                GenScene.GoToMainMenu();
            return;
        }
        Travelling = true;
        _leaving = Current.Game;
        GameDataSaveLoader.LoadGame(HomeSave);
    }

    // ---------- the raiders' own buttons ----------

    public static IEnumerable<Gizmo> Gizmos(Pawn pawn)
    {
        if (CoopGuest.VisitHelp)
        {
            yield return GoHome(help: true);
            yield break;
        }
        yield return new Command_Action
        {
            defaultLabel = "RimMult.VisitRetreat".Translate(),
            defaultDesc = "RimMult.VisitRetreatDesc".Translate(),
            icon = Icon("UI/Commands/FormCaravan"),
            action = () =>
            {
                if (!RCellFinder.TryFindBestExitSpot(pawn, out var exit))
                    exit = CellFinder.RandomEdgeCell(pawn.Map);
                var job = JobMaker.MakeJob(JobDefOf.Goto, exit);
                job.exitMapOnArrival = true;
                pawn.jobs.TryTakeOrderedJob(job, JobTag.Misc);
            },
        };
        yield return new Command_Target
        {
            defaultLabel = "RimMult.VisitKidnap".Translate(),
            defaultDesc = "RimMult.VisitKidnapDesc".Translate(),
            icon = Icon("UI/Commands/Capture"),
            targetingParams = new TargetingParameters
            {
                canTargetPawns = true,
                canTargetBuildings = false,
                validator = t => t.Thing is Pawn { Downed: true } victim && victim.RaceProps.Humanlike && !IsMine(victim),
            },
            action = target => pawn.jobs.TryTakeOrderedJob(JobMaker.MakeJob(JobDefOf.Kidnap, target), JobTag.Misc),
        };
        yield return new Command_Target
        {
            defaultLabel = "RimMult.VisitSteal".Translate(),
            defaultDesc = "RimMult.VisitStealDesc".Translate(),
            icon = Icon("UI/Commands/Steal"),
            targetingParams = new TargetingParameters
            {
                canTargetPawns = false,
                canTargetBuildings = false,
                canTargetItems = true,
                mapObjectTargetsMustBeAutoAttackable = false,
                validator = t => t.Thing is { def.EverHaulable: true },
            },
            action = target => pawn.jobs.TryTakeOrderedJob(JobMaker.MakeJob(JobDefOf.Steal, target), JobTag.Misc),
        };
        yield return GoHome(help: false);
    }

    private static Command GoHome(bool help) => new Command_Action
    {
        defaultLabel = "RimMult.VisitGoHome".Translate(),
        defaultDesc = (help ? "RimMult.HelpGoHomeDesc" : "RimMult.VisitGoHomeDesc").Translate(),
        icon = Icon("UI/Commands/ReturnToShip"),
        action = () => Find.WindowStack.Add(Dialog_MessageBox.CreateConfirmation(
            (help ? "RimMult.HelpGoHomeConfirm" : "RimMult.VisitGoHomeConfirm").Translate(), () => ReturnHome(null), destructive: !help)),
    };

    private static Texture2D Icon(string path) => ContentFinder<Texture2D>.Get(path, reportFailure: false) ?? BaseContent.BadTex;
}

/// <summary>Visitor: orders to the raiders become jobs for the defender's game (nothing happens in the copy itself).</summary>
[HarmonyPatch(typeof(Pawn_JobTracker), nameof(Pawn_JobTracker.TryTakeOrderedJob))]
internal static class VisitJobPatch
{
    private static float _lastRefused;

    private static bool Prefix(Pawn_JobTracker __instance, Job job, bool requestQueueing, ref bool __result)
    {
        if (job == null || CoopGuest.Applying)
            return true;
        if (CoopGuest.Visiting && CoopGuest.Active)
        {
            __result = PlayerVisit.RelayJob(__instance, job, requestQueueing);
            return false;
        }
        // Visited: an ally's helpers take orders from the ally only.
        if (!PlayerVisits.Ordering && PlayerVisit.PawnOf(__instance) is { } pawn && PlayerVisits.IsControlled(pawn))
        {
            if (Time.realtimeSinceStartup - _lastRefused > 2f)
            {
                _lastRefused = Time.realtimeSinceStartup;
                Messages.Message("RimMult.HelpControlledBy".Translate(PlayerVisits.LeaderOf(pawn) ?? "?"), pawn, MessageTypeDefOf.RejectInput, historical: false);
            }
            __result = false;
            return false;
        }
        return true;
    }
}

/// <summary>Visitor: the raiders get buttons to retreat, kidnap, steal and go home.</summary>
[HarmonyPatch(typeof(Pawn), nameof(Pawn.GetGizmos))]
internal static class VisitGizmosPatch
{
    private static IEnumerable<Gizmo> Postfix(IEnumerable<Gizmo> gizmos, Pawn __instance)
    {
        foreach (var gizmo in gizmos)
            yield return gizmo;
        if (CoopGuest.Visiting && CoopGuest.Active && PlayerVisit.IsMine(__instance) && __instance.Spawned)
            foreach (var gizmo in PlayerVisit.Gizmos(__instance))
                yield return gizmo;
    }
}

/// <summary>
/// What a pawn is doing is the commander's secret: the defender doesn't see the visitor's orders to its raiders,
/// the visitor doesn't see what the defender's people were told.
/// </summary>
[HarmonyPatch]
[Patches.LatePatch]
internal static class VisitHiddenJobPatch
{
    private static IEnumerable<MethodBase> TargetMethods()
    {
        foreach (var type in typeof(JobDriver).AllSubclasses().Prepend(typeof(JobDriver)))
        {
            if (type.ContainsGenericParameters)
                continue;
            var method = AccessTools.DeclaredMethod(type, nameof(JobDriver.GetReport), Type.EmptyTypes);
            if (method != null && !method.IsAbstract)
                yield return method;
        }
    }

    private static void Postfix(JobDriver __instance, ref string __result)
    {
        var pawn = __instance.pawn;
        if (pawn == null)
            return;
        var hide = CoopGuest.Visiting
            ? !CoopGuest.VisitHelp && !PlayerVisit.IsMine(pawn) && pawn.RaceProps.Humanlike
            : PlayerVisits.IsControlledRaider(pawn);
        if (hide)
            __result = "RimMult.VisitHiddenJob".Translate();
    }
}

/// <summary>A caravan at an ally's colony goes in to help, led by this player in person.</summary>
public sealed class CaravanArrivalAction_HelpPlayer : CaravanArrivalAction
{
    private ParcelTarget _target = new();

    public CaravanArrivalAction_HelpPlayer()
    {
    }

    public CaravanArrivalAction_HelpPlayer(RemoteColony colony)
    {
        _target = new ParcelTarget(colony);
    }

    public override string Label => "RimMult.HelpGo".Translate(_target.OwnerName);

    public override string ReportString => "RimMult.HelpGoReport".Translate(_target.OwnerName);

    public override FloatMenuAcceptanceReport StillValid(Caravan caravan, PlanetTile destinationTile) =>
        Colony() is { } colony ? PlayerVisits.CanHelp(colony, caravan.PawnsListForReading) : false;

    private RemoteColony? Colony() =>
        Find.WorldObjects.AllWorldObjects.OfType<RemoteColony>().FirstOrDefault(c => c.OwnerSteamId == _target.OwnerSteamId);

    public override void Arrived(Caravan caravan)
    {
        var colony = Colony();
        var allowed = colony != null ? PlayerVisits.CanHelp(colony, caravan.PawnsListForReading) : FloatMenuAcceptanceReport.WithFailReason("RimMult.RaidOwnerAway".Translate(_target.OwnerName));
        if (!allowed.Accepted)
        {
            Messages.Message("RimMult.HelpCancelled".Translate(_target.OwnerName, allowed.FailReason), caravan, MessageTypeDefOf.RejectInput, historical: false);
            return;
        }
        var pawns = caravan.PawnsListForReading.ToList();
        if (!PlayerVisits.LaunchHelp(_target, ParcelAddress.RaidArrival.WalkIn, pawns))
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

/// <summary>Transport pods launched at an ally's colony: the passengers land there to help, led by this player in person.</summary>
public sealed class TransportersArrivalAction_HelpPlayer : TransportersArrivalAction
{
    private ParcelTarget _target = new();

    public TransportersArrivalAction_HelpPlayer()
    {
    }

    public TransportersArrivalAction_HelpPlayer(RemoteColony colony)
    {
        _target = new ParcelTarget(colony);
    }

    public override bool GeneratesMap => false;

    public override FloatMenuAcceptanceReport StillValid(IEnumerable<IThingHolder> pods, PlanetTile destinationTile)
    {
        var colony = Find.WorldObjects.AllWorldObjects.OfType<RemoteColony>().FirstOrDefault(c => c.OwnerSteamId == _target.OwnerSteamId);
        return colony != null ? PlayerVisits.CanHelp(colony, pods.SelectMany(p => p.GetDirectlyHeldThings()).OfType<Pawn>().ToList()) : false;
    }

    public override void Arrived(List<ActiveTransporterInfo> transporters, PlanetTile tile)
    {
        var contents = transporters.SelectMany(t => t.innerContainer).ToList();
        var pawns = contents.OfType<Pawn>().ToList();
        var colony = Find.WorldObjects.AllWorldObjects.OfType<RemoteColony>().FirstOrDefault(c => c.OwnerSteamId == _target.OwnerSteamId);
        if (colony == null || pawns.Count == 0 || !PlayerVisits.CanHelp(colony, pawns).Accepted)
        {
            // Can't help after all (the ally left, the alliance ended): the pods come back down at home.
            if (Find.AnyPlayerHomeMap is { } home)
                foreach (var transporter in transporters)
                    transporter.innerContainer.TryDropAll(DropCellFinder.TradeDropSpot(home), home, ThingPlaceMode.Near);
            Messages.Message("RimMult.HelpCancelled".Translate(_target.OwnerName, ""), MessageTypeDefOf.RejectInput, historical: false);
            return;
        }

        // Cargo goes along in the helpers' packs.
        var carriers = pawns.Where(p => p.RaceProps.Humanlike && p.inventory != null).ToList();
        var index = 0;
        foreach (var item in contents.Where(t => t is not Pawn).ToList())
        {
            if (carriers.Count == 0)
                break;
            transporters.First(t => t.innerContainer.Contains(item)).innerContainer.Remove(item);
            carriers[index++ % carriers.Count].inventory.innerContainer.TryAdd(item);
        }

        if (!PlayerVisits.LaunchHelp(_target, ParcelAddress.RaidArrival.DropPods, pawns))
            return;
        TransportersArrivalActionUtility.RemovePawnsFromWorldPawns(transporters);
        foreach (var transporter in transporters)
            transporter.innerContainer.ClearAndDestroyContents(DestroyMode.Vanish);
    }

    public override void ExposeData()
    {
        base.ExposeData();
        Scribe_Deep.Look(ref _target, "target");
        _target ??= new ParcelTarget();
    }
}
