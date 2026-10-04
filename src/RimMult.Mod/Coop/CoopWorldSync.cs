using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using RimMult.Shared.Coop;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace RimMult.Coop;

/// <summary>
/// Co-op is one colony, so its NPC world is shared too: relations with the factions, research, the letters the
/// storyteller sends, the colony's policies and its caravans on the globe all come from the host's game and show up on the guests' side as they happen.
/// (In separate colonies each colony keeps its own relations, quests and traders.)
/// </summary>
internal static class CoopWorldSync
{
    private const float CheckInterval = 1f;

    /// <summary>Research progress counts as changed once it moved this many points (or the project finished).</summary>
    private const float ResearchStep = 1f;

    private static readonly AccessTools.FieldRef<ResearchManager, Dictionary<ResearchProjectDef, float>> Progress =
        AccessTools.FieldRefAccess<ResearchManager, Dictionary<ResearchProjectDef, float>>("progress");

    private static readonly AccessTools.FieldRef<ResearchManager, ResearchProjectDef?> CurrentProject =
        AccessTools.FieldRefAccess<ResearchManager, ResearchProjectDef?>("currentProj");

    private static readonly List<CoopLetter> Letters = new();
    private static readonly Dictionary<string, float> SentProgress = new();
    private static int? _factionsHash;
    private static int? _policiesHash;
    private static int? _questsSignature;
    private static string? _sentCurrent;
    private static float _lastCheck;

    /// <summary>A guest just loaded the game: relations, research and the current project go out in full again.</summary>
    public static void Forget()
    {
        _factionsHash = null;
        _policiesHash = null;
        _questsSignature = null;
        CoopGlobe.Forget();
        ModSync.Forget();
        _sentCurrent = null;
        SentProgress.Clear();
    }

    public static void Reset()
    {
        Forget();
        Letters.Clear();
    }

    // ---------- host ----------

    public static void QueueLetter(Letter letter)
    {
        if (Letters.Count >= 50)
            return;
        var shared = new CoopLetter
        {
            Label = letter.Label.Resolve(),
            Text = (letter as ChoiceLetter)?.Text.Resolve() ?? "",
            Def = letter.def?.defName ?? "",
        };
        var target = letter.lookTargets?.PrimaryTarget ?? GlobalTargetInfo.Invalid;
        if (target.IsValid && target.IsMapTarget && target.Map != null)
        {
            shared.MapId = target.Map.uniqueID;
            shared.X = target.Cell.x;
            shared.Z = target.Cell.z;
        }
        Letters.Add(shared);
    }

    /// <summary>What changed in the NPC world since the last batch.</summary>
    public static CoopWorld Build()
    {
        var world = new CoopWorld();
        world.Letters.AddRange(Letters);
        Letters.Clear();
        CoopGlobe.Build(world);

        var now = Time.realtimeSinceStartup;
        if (now - _lastCheck < CheckInterval)
            return world;
        _lastCheck = now;

        var player = Faction.OfPlayer;
        var standings = Find.FactionManager.AllFactionsListForReading
            .Where(f => f != player && f.RelationWith(player, allowNull: true) != null)
            .Select(f =>
            {
                var relation = f.RelationWith(player);
                return new FactionStanding(f.GetUniqueLoadID(), relation.baseGoodwill, (byte)relation.kind);
            })
            .ToList();
        var hash = standings.Aggregate(17, (h, s) => unchecked(h * 31 + s.FactionId.GetHashCode() * 7 + s.Goodwill * 3 + s.Kind));
        if (_factionsHash != hash)
        {
            _factionsHash = hash;
            world.Factions = standings;
        }

        var research = new List<(string, float)>();
        foreach (var pair in Progress(Find.ResearchManager))
        {
            var name = pair.Key.defName;
            if (!SentProgress.TryGetValue(name, out var sent) || Mathf.Abs(sent - pair.Value) >= ResearchStep
                || (pair.Value >= pair.Key.baseCost && sent < pair.Key.baseCost))
            {
                SentProgress[name] = pair.Value;
                research.Add((name, pair.Value));
            }
        }
        if (research.Count > 0)
            world.Research = research;

        var current = CurrentProject(Find.ResearchManager)?.defName ?? "";
        if (current != _sentCurrent)
        {
            _sentCurrent = current;
            world.CurrentResearch = current;
        }

        // Quests: a cheap signature every check, the whole quest log when it changed (a guest got the log with the game).
        var questsSignature = QuestsSignature();
        if (_questsSignature == null)
            _questsSignature = questsSignature;
        else if (questsSignature != _questsSignature)
        {
            _questsSignature = questsSignature;
            world.Quests = ScribeMemory.Save(Find.QuestManager.ExposeData);
        }

        ModSync.Build(world);

        var policies = CoopEdits.SavePolicies();
        var policiesHash = policies.GetHashCode() ^ policies.Length;
        if (policiesHash != _policiesHash)
        {
            _policiesHash = policiesHash;
            world.Policies = policies;
        }
        return world;
    }

    // ---------- guest ----------

    public static void Apply(CoopWorld world)
    {
        try
        {
            if (world.Factions != null)
                ApplyFactions(world.Factions);
            if (world.Research != null)
            {
                var progress = Progress(Find.ResearchManager);
                foreach (var (project, points) in world.Research)
                {
                    if (DefDatabase<ResearchProjectDef>.GetNamedSilentFail(project) is { } def)
                        progress[def] = points;
                }
            }
            if (world.CurrentResearch != null)
                CurrentProject(Find.ResearchManager) = world.CurrentResearch.Length == 0
                    ? null
                    : DefDatabase<ResearchProjectDef>.GetNamedSilentFail(world.CurrentResearch);
            foreach (var letter in world.Letters)
                ShowLetter(letter);
            if (world.Policies != null)
                CoopEdits.ApplyPolicies(world.Policies);
            if (world.Quests != null)
                ApplyQuests(world.Quests);
            if (world.ModStates.Count > 0)
                ModSync.Apply(world.ModStates);
            CoopGlobe.Apply(world);
        }
        catch (Exception e)
        {
            Log.ErrorOnce($"[RimMult] Co-op: could not apply the host's world: {e}", 0x434F574C);
        }
    }

    private static int QuestsSignature()
    {
        unchecked
        {
            var hash = 17;
            foreach (var quest in Find.QuestManager.QuestsListForReading)
            {
                hash = hash * 31 + quest.id;
                hash = hash * 31 + (int)quest.State;
                hash = hash * 31 + (quest.dismissed ? 1 : 0) + (quest.hidden ? 2 : 0) + (quest.hiddenInUI ? 4 : 0);
            }
            return hash;
        }
    }

    /// <summary>Guest: the host's quest log replaces the copy's (offers, accepted, finished).</summary>
    private static void ApplyQuests(string xml)
    {
        try
        {
            var fresh = new QuestManager();
            var replaced = new HashSet<string>(Find.QuestManager.QuestsListForReading.Select(q => q.GetUniqueLoadID()));
            ScribeMemory.Load(ScribeMemory.Parse(xml), replaced, fresh.ExposeData);
            Current.Game.questManager = fresh;
        }
        catch (Exception e)
        {
            Log.WarningOnce($"[RimMult] Co-op: could not take over the host's quests: {e}", 0x434F5155);
        }
    }

    /// <summary>Host: a guest accepted a quest in its quests tab.</summary>
    public static void AcceptQuest(CoopCommand command)
    {
        var quest = Find.QuestManager.QuestsListForReading.FirstOrDefault(q => q.id == command.Number);
        if (quest == null || quest.State != QuestState.NotYetAccepted)
            return;
        Pawn? by = null;
        if (command.ThingIds.Count > 0)
            by = Find.Maps.SelectMany(m => m.mapPawns.AllPawns).FirstOrDefault(p => p.thingIDNumber == command.ThingIds[0]);
        quest.Accept(by);
    }

    private static void ApplyFactions(List<FactionStanding> standings)
    {
        var player = Faction.OfPlayer;
        var byId = Find.FactionManager.AllFactionsListForReading.ToDictionary(f => f.GetUniqueLoadID());
        foreach (var standing in standings)
        {
            if (!byId.TryGetValue(standing.FactionId, out var faction))
                continue;
            foreach (var relation in new[] { faction.RelationWith(player, allowNull: true), player.RelationWith(faction, allowNull: true) })
            {
                if (relation == null)
                    continue;
                relation.baseGoodwill = standing.Goodwill;
                relation.kind = (FactionRelationKind)standing.Kind;
            }
        }
    }

    private static void ShowLetter(CoopLetter shared)
    {
        var def = DefDatabase<LetterDef>.GetNamedSilentFail(shared.Def) ?? LetterDefOf.NeutralEvent;
        var look = LookTargets.Invalid;
        var map = Find.Maps.FirstOrDefault(m => m.uniqueID == shared.MapId);
        var cell = new IntVec3(shared.X, 0, shared.Z);
        if (map != null && cell.InBounds(map))
            look = new LookTargets(new TargetInfo(cell, map));
        Find.LetterStack.ReceiveLetter(LetterMaker.MakeLetter(shared.Label, shared.Text, def, look));
    }
}

/// <summary>Guest: accepting a quest in the quests tab accepts it in the host's game.</summary>
[HarmonyPatch(typeof(Quest), nameof(Quest.Accept))]
internal static class CoopQuestAcceptPatch
{
    private static bool Prefix(Quest __instance, Pawn by)
    {
        if (!CoopGuest.Active || CoopGuest.Applying || CoopGuest.Visiting)
            return true;
        var command = new CoopCommand { Kind = CoopCommandKind.QuestAccept, Number = __instance.id };
        if (by != null)
            command.ThingIds.Add(by.thingIDNumber);
        CoopCommands.Send(command);
        return false;
    }
}

/// <summary>Host: every letter the colony receives also goes to the co-op guests.</summary>
[HarmonyPatch(typeof(LetterStack), nameof(LetterStack.ReceiveLetter), new[] { typeof(Letter), typeof(string), typeof(int), typeof(bool) })]
internal static class CoopLetterPatch
{
    private static void Postfix(Letter let)
    {
        if (!CoopHost.Active || let == null || Multiplayer.Session?.Mode != GameMode.Coop)
            return;
        try
        {
            CoopWorldSync.QueueLetter(let);
        }
        catch (Exception e)
        {
            Log.WarningOnce($"[RimMult] Co-op: could not share a letter: {e.Message}", 0x434F4C54);
        }
    }
}
