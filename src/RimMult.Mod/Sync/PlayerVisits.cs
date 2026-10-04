using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using RimMult.ClientCore;
using RimMult.Coop;
using RimMult.Shared.Coop;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;
using Verse.AI.Group;

namespace RimMult.Sync;

/// <summary>
/// Live raids, defender's side. The attacker joins this game as a visitor (the co-op machinery, between two players)
/// and commands its raiders here: their AI lord lets go of them, the visitor's orders arrive as jobs, and the raiders
/// hold their ground (shooting what comes near) between orders. This player sees them move and fight like any
/// raiders, not what they were told. When the visitor leaves, the AI takes over again.
/// </summary>
internal static class PlayerVisits
{
    private const float ControlInterval = 0.5f;

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

    /// <summary>Visitor (player id) → the live raid it leads.</summary>
    private static readonly Dictionary<int, RaidRecord> Visitors = new();

    private static float _lastControl;

    /// <summary>The live raid this player sent here, if any.</summary>
    private static RaidRecord? RaidOf(ClientSession session, int playerId)
    {
        var player = session.Players.FirstOrDefault(p => p.Id == playerId);
        if (player == null || RimMultGameComp.Instance is not { } comp)
            return null;
        var owner = Multiplayer.OwnerKey(player);
        return comp.Raids.FirstOrDefault(r => r.Live && r.AttackerOwner == owner);
    }

    /// <summary>A player may visit while its live raid is on (its raiders have arrived).</summary>
    public static bool CanHost(ClientSession session, int playerId) => RaidOf(session, playerId) != null;

    public static bool IsControlled(Pawn pawn)
    {
        foreach (var raid in Visitors.Values)
            if (raid.Raiders.Contains(pawn))
                return true;
        return false;
    }

    /// <summary>The visitor got this game: its raiders are its own from now on.</summary>
    public static void Joined(ClientSession session, int playerId)
    {
        var raid = RaidOf(session, playerId);
        if (raid == null)
            return;
        Visitors[playerId] = raid;
        foreach (var raider in raid.Raiders.Where(p => p != null))
        {
            raider.GetLord()?.RemovePawn(raider);
            if (raider.Spawned && !raider.Dead)
                raider.jobs?.EndCurrentJob(JobCondition.InterruptForced);
        }
        session.SendCoop(CoopChannel.VisitInfo, CoopIds.Encode(raid.Raiders.Where(p => p != null).Select(p => p.thingIDNumber).ToList()), playerId);
        Messages.Message("RimMult.VisitJoining".Translate(raid.AttackerName), MessageTypeDefOf.ThreatBig, historical: false);
    }

    /// <summary>The visitor left (went home, lost connection): the raid goes on under this game's AI.</summary>
    public static void Left(int playerId)
    {
        if (!Visitors.TryGetValue(playerId, out var raid))
            return;
        Visitors.Remove(playerId);
        if (RimMultGameComp.Instance?.Raids.Contains(raid) != true)
            return;
        var fighters = raid.Raiders.Where(p => p is { Spawned: true, Dead: false, Downed: false } && p.GetLord() == null).ToList();
        if (fighters.Count > 0)
        {
            var faction = fighters[0].Faction;
            foreach (var pawn in fighters)
                pawn.jobs?.EndCurrentJob(JobCondition.InterruptForced);
            LordMaker.MakeNewLord(faction, new LordJob_AssaultColony(faction, canKidnap: true, canTimeoutOrFlee: true, canSteal: true), fighters[0].Map, fighters);
        }
        Messages.Message("RimMult.VisitLeft".Translate(raid.AttackerName), MessageTypeDefOf.NeutralEvent, historical: false);
    }

    /// <summary>Keeps idle raiders holding their ground, and tells visitors whose raid is over to go home.</summary>
    public static void Update(ClientSession session)
    {
        if (Visitors.Count == 0)
            return;
        var now = Time.realtimeSinceStartup;
        if (now - _lastControl < ControlInterval || Current.ProgramState != ProgramState.Playing)
            return;
        _lastControl = now;

        var raids = RimMultGameComp.Instance?.Raids;
        foreach (var pair in Visitors.ToList())
        {
            if (raids == null || !raids.Contains(pair.Value))
            {
                // The raid is over (and its survivors are on their way back): the visit ends with it.
                Visitors.Remove(pair.Key);
                CoopHost.DropGuest(pair.Key);
                session.SendCoop(CoopChannel.VisitEnd, Array.Empty<byte>(), pair.Key);
                continue;
            }
            foreach (var raider in pair.Value.Raiders)
            {
                if (raider is not { Spawned: true, Dead: false, Downed: false } || raider.InMentalState || raider.jobs == null)
                    continue;
                if (raider.CurJob == null || !raider.CurJob.playerForced)
                    raider.jobs.TryTakeOrderedJob(JobMaker.MakeJob(JobDefOf.Wait_Combat), JobTag.Misc);
            }
        }
    }

    /// <summary>An order from a visitor: a job for one of its raiders (anything else is refused).</summary>
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
        if (command.Kind != CoopCommandKind.Job || command.ThingIds.Count == 0 || !Visitors.TryGetValue(playerId, out var raid))
            return;
        var pawn = raid.Raiders.FirstOrDefault(p => p != null && p.thingIDNumber == command.ThingIds[0]);
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

        if (KidnapJobs.Contains(job.def.defName) && job.targetA.Thing is Pawn victim)
            job = JobMaker.MakeJob(JobDefOf.Kidnap, victim);
        if (job.ability == null && !AllowedJobs.Contains(job.def.defName))
        {
            Log.Message($"[RimMult] Visit: order {job.def.defName} is not for raiders, ignored.");
            return;
        }
        // Carrying someone or something off needs a way out: the nearest edge.
        if ((job.def == JobDefOf.Kidnap || job.def == JobDefOf.Steal) && !job.targetB.IsValid && RCellFinder.TryFindBestExitSpot(pawn, out var exit))
            job.targetB = exit;
        if (job.def == JobDefOf.Kidnap || job.def == JobDefOf.Steal)
            job.count = Math.Max(1, job.targetA.Thing?.stackCount ?? 1);

        JobLoadId(job) = Find.UniqueIDsManager.GetNextJobID();
        job.playerForced = true;
        pawn.jobs.TryTakeOrderedJob(job, JobTag.Misc, command.Queue);
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

    /// <summary>The defender's game is loaded: the raiders become this player's pawns, the defender's colony the enemy.</summary>
    public static void CopyLoaded()
    {
        var enemy = Enemy();
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
        Find.LetterStack.ReceiveLetter("RimMult.VisitStartedLabel".Translate(), "RimMult.VisitStartedText".Translate(), LetterDefOf.NeutralEvent, new LookTargets(mine));
    }

    /// <summary>A thing the defender's game just sent, before it spawns: the same swap of sides.</summary>
    public static void AdjustLoaded(Thing thing) => Adjust(thing, Enemy());

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
    public static bool RelayJob(Pawn_JobTracker tracker, Job job, bool queue)
    {
        var pawn = TrackerPawn(tracker);
        if (pawn == null || !IsMine(pawn))
            return false;
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
        yield return new Command_Action
        {
            defaultLabel = "RimMult.VisitGoHome".Translate(),
            defaultDesc = "RimMult.VisitGoHomeDesc".Translate(),
            icon = Icon("UI/Commands/ReturnToShip"),
            action = () => Find.WindowStack.Add(Dialog_MessageBox.CreateConfirmation(
                "RimMult.VisitGoHomeConfirm".Translate(), () => ReturnHome(null), destructive: true)),
        };
    }

    private static Texture2D Icon(string path) => ContentFinder<Texture2D>.Get(path, reportFailure: false) ?? BaseContent.BadTex;
}

/// <summary>Visitor: orders to the raiders become jobs for the defender's game (nothing happens in the copy itself).</summary>
[HarmonyPatch(typeof(Pawn_JobTracker), nameof(Pawn_JobTracker.TryTakeOrderedJob))]
internal static class VisitJobPatch
{
    private static bool Prefix(Pawn_JobTracker __instance, Job job, bool requestQueueing, ref bool __result)
    {
        if (!CoopGuest.Visiting || !CoopGuest.Active || CoopGuest.Applying || job == null)
            return true;
        __result = PlayerVisit.RelayJob(__instance, job, requestQueueing);
        return false;
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
        var hide = CoopGuest.Visiting ? !PlayerVisit.IsMine(pawn) && pawn.RaceProps.Humanlike : PlayerVisits.IsControlled(pawn);
        if (hide)
            __result = "RimMult.VisitHiddenJob".Translate();
    }
}
