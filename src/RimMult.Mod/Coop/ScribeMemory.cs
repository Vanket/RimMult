using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using HarmonyLib;
using RimWorld;
using Verse;

namespace RimMult.Coop;

/// <summary>
/// The game's save system, in memory and in small pieces: one thing (or a few map grids) at a time on the host,
/// loaded on a guest with references resolved against the objects the guest's copy of the game already has.
/// Co-op mirrors the host's game id for id, so a reference like "Faction_3" or "Thing_Human512" means the same
/// object on both sides.
/// </summary>
internal static class ScribeMemory
{
    private static readonly AccessTools.FieldRef<ScribeSaver, Stream?> SaveStream = AccessTools.FieldRefAccess<ScribeSaver, Stream?>("saveStream");
    private static readonly AccessTools.FieldRef<ScribeSaver, XmlWriter?> Writer = AccessTools.FieldRefAccess<ScribeSaver, XmlWriter?>("writer");
    private static readonly AccessTools.FieldRef<CrossRefHandler, LoadedObjectDirectory> Directory = AccessTools.FieldRefAccess<CrossRefHandler, LoadedObjectDirectory>("loadedObjectDirectory");
    private static readonly Regex TrailingNumber = new(@"(\d+)$", RegexOptions.Compiled);

    /// <summary>Whether the save system is free (not in the middle of a real save or load).</summary>
    public static bool Idle => Scribe.mode == LoadSaveMode.Inactive;

    private static readonly AccessTools.FieldRef<Corpse, ThingOwner<Pawn>> CorpseContainer = AccessTools.FieldRefAccess<Corpse, ThingOwner<Pawn>>("innerContainer");

    /// <summary>One thing as a standalone fragment: <c>&lt;root&gt;&lt;li Class="..."&gt;…&lt;/li&gt;&lt;/root&gt;</c>.</summary>
    public static string SaveThing(Thing thing)
    {
        // A corpse only refers to its dead pawn, which the host keeps among its world pawns, out of a guest's reach
        // (the corpse then spawns "in a bugged state" and is destroyed): the pawn is saved inside the corpse instead.
        var corpses = new List<(ThingOwner<Pawn> Container, LookMode Mode)>();
        foreach (var corpse in CorpsesIn(thing))
        {
            var container = CorpseContainer(corpse);
            if (container != null && container.contentsLookMode != LookMode.Deep)
            {
                corpses.Add((container, container.contentsLookMode));
                container.contentsLookMode = LookMode.Deep;
            }
        }
        try
        {
            return Save(() => Scribe_Deep.Look(ref thing, "li"));
        }
        finally
        {
            foreach (var (container, mode) in corpses)
                container.contentsLookMode = mode;
        }
    }

    /// <summary>The thing if it is a corpse, and the corpses it holds (one a pawn carries, …).</summary>
    private static IEnumerable<Corpse> CorpsesIn(Thing thing)
    {
        if (thing is Corpse corpse)
            yield return corpse;
        if (thing is not IThingHolder holder)
            yield break;
        var held = new List<Thing>();
        try
        {
            ThingOwnerUtility.GetAllThingsRecursively(holder, held, allowUnreal: false);
        }
        catch (Exception)
        {
            yield break;
        }
        foreach (var inner in held)
            if (inner is Corpse innerCorpse && innerCorpse != thing)
                yield return innerCorpse;
    }

    /// <summary>
    /// References a guest can't have: battles and combat log entries live in the host's logs only. They are cut from a
    /// fragment before loading (a pawn just doesn't show which battle it is in), instead of failing to resolve.
    /// </summary>
    public static void StripHostOnlyReferences(XmlNode fragment)
    {
        var nodes = fragment.SelectNodes(".//*[not(*)][starts-with(normalize-space(text()), 'Battle_') or starts-with(normalize-space(text()), 'LogEntry_')]");
        if (nodes == null)
            return;
        foreach (XmlNode node in nodes)
            node.InnerText = "null";
    }

    /// <summary>Runs <paramref name="expose"/> (ExposeData calls) in saving mode and returns the XML.</summary>
    public static string Save(Action expose)
    {
        using var stream = new MemoryStream();
        var writer = XmlWriter.Create(stream, new XmlWriterSettings { Indent = false, OmitXmlDeclaration = true, Encoding = new UTF8Encoding(false) });
        SaveStream(Scribe.saver) = null;
        Writer(Scribe.saver) = writer;
        Scribe.mode = LoadSaveMode.Saving;
        Scribe.saver.savingForDebug = true; // skips the "referenced but not saved" checks meant for whole saves
        try
        {
            writer.WriteStartDocument();
            writer.WriteStartElement("root");
            expose();
            writer.WriteEndElement();
            writer.WriteEndDocument();
            writer.Flush();
        }
        finally
        {
            Scribe.saver.ForceStop();
        }
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    /// <summary>The thing id a fragment from <see cref="SaveThing"/> carries, read without loading it.</summary>
    public static int? FragmentThingId(XmlNode li)
    {
        var id = li["id"]?.InnerText;
        if (id == null)
            return null;
        var match = TrailingNumber.Match(id);
        return match.Success && int.TryParse(match.Groups[1].Value, out var number) ? number : null;
    }

    /// <summary>The load id ("Thing_Steel123") of the thing a fragment from <see cref="SaveThing"/> carries.</summary>
    public static string? FragmentLoadId(XmlNode li) => li["id"]?.InnerText is { } id ? "Thing_" + id : null;

    /// <summary>
    /// Loads things from fragments. <paramref name="replacedIds"/> are load ids of existing objects the loaded ones
    /// will replace: they are kept out of the reference directory so the newcomers can take their ids.
    /// </summary>
    public static List<Thing> LoadThings(IEnumerable<XmlNode> items, ISet<string> replacedIds)
    {
        var doc = new XmlDocument();
        var root = doc.CreateElement("root");
        doc.AppendChild(root);
        var list = doc.CreateElement("things");
        root.AppendChild(list);
        foreach (var item in items)
            list.AppendChild(doc.ImportNode(item, true));

        List<Thing>? things = null;
        Load(root, replacedIds, () => Scribe_Collections.Look(ref things, "things", LookMode.Deep));
        return things?.Where(t => t != null).ToList() ?? new List<Thing>();
    }

    /// <summary>
    /// Runs <paramref name="expose"/> in loading mode over <paramref name="root"/>, then resolves and post-loads
    /// (with <paramref name="beforePostLoad"/> in between, for links the post-load step relies on).
    /// </summary>
    public static void Load(XmlNode root, ISet<string> replacedIds, Action expose, Action? beforePostLoad = null)
    {
        Scribe.mode = LoadSaveMode.LoadingVars;
        Scribe.loader.curXmlParent = root;
        Scribe.loader.curParent = null;
        Scribe.loader.curPathRelToParent = null;
        try
        {
            RegisterReferenced(root, replacedIds);
            expose();
            Scribe.mode = LoadSaveMode.ResolvingCrossRefs;
            Scribe.loader.crossRefs.ResolveAllCrossReferences();
            beforePostLoad?.Invoke();
            Scribe.mode = LoadSaveMode.PostLoadInit;
            Scribe.loader.initer.DoAllPostLoadInits();
        }
        finally
        {
            Scribe.loader.ForceStop();
        }
    }

    public static XmlElement Parse(string xml)
    {
        var doc = new XmlDocument();
        doc.LoadXml(xml);
        return doc.DocumentElement!;
    }

    // Load id → existing object. Rebuilding it walks the whole game, so it is kept and topped up instead:
    // things the guest spawns are added as they come, and a full rebuild happens only now and then, or when a
    // fragment refers to something the cache doesn't know yet.
    private static readonly Dictionary<string, ILoadReferenceable> Known = new();

    // "Thing", "Faction", … : a token only counts as an unknown id if it looks like one (def names have '_' too).
    private static readonly HashSet<string> Prefixes = new();
    private static float _knownBuilt = float.NegativeInfinity;
    private const float RebuildEvery = 10f;
    private const float RebuildAtMostEvery = 1f;

    /// <summary>Forget the cache (another game loaded).</summary>
    public static void ResetCache()
    {
        Known.Clear();
        Prefixes.Clear();
        _knownBuilt = float.NegativeInfinity;
    }

    /// <summary>A thing (with a pawn's gear) the cache should know from now on.</summary>
    public static void Remember(Thing thing)
    {
        TryAdd(thing);
        if (thing is Pawn pawn)
            foreach (var gear in PawnGear(pawn))
                TryAdd(gear);
    }

    private static void TryAdd(ILoadReferenceable reffable)
    {
        try
        {
            var id = reffable.GetUniqueLoadID();
            Known[id] = reffable;
            var bar = id.IndexOf('_');
            if (bar > 0)
                Prefixes.Add(id.Substring(0, bar));
        }
        catch (Exception)
        {
            // An object that can't name itself can't be referenced either.
        }
    }

    private static void Rebuild()
    {
        Known.Clear();
        foreach (var reffable in Referenceables())
            TryAdd(reffable);
        _knownBuilt = UnityEngine.Time.realtimeSinceStartup;
    }

    /// <summary>
    /// Puts the existing objects a fragment refers to (by load id, e.g. "Faction_3") into the loader's directory.
    /// Only those: registering everything for every fragment is what made guests slow.
    /// </summary>
    private static void RegisterReferenced(XmlNode root, ISet<string> skip)
    {
        var now = UnityEngine.Time.realtimeSinceStartup;
        if (now - _knownBuilt > RebuildEvery)
            Rebuild();

        var tokens = new HashSet<string>();
        CollectTokens(root, tokens);
        tokens.ExceptWith(skip);

        var missing = tokens.Any(t => !Known.ContainsKey(t) && Prefixes.Contains(t.Substring(0, t.IndexOf('_'))));
        if (missing && now - _knownBuilt > RebuildAtMostEvery)
            Rebuild();

        var directory = Directory(Scribe.loader.crossRefs);
        foreach (var token in tokens)
        {
            if (Known.TryGetValue(token, out var reffable))
                directory.RegisterLoaded(reffable);
        }
    }

    /// <summary>Every short text value that could be a load id ("Thing_Steel123", "Faction_5", …).</summary>
    private static void CollectTokens(XmlNode node, HashSet<string> tokens)
    {
        foreach (XmlNode child in node.ChildNodes)
        {
            if (child.NodeType == XmlNodeType.Text)
            {
                var value = child.Value?.Trim();
                if (value != null && value.Length < 128 && value.IndexOf('_') > 0)
                    tokens.Add(value);
            }
            else if (child.HasChildNodes)
            {
                CollectTokens(child, tokens);
            }
        }
    }

    private static IEnumerable<ILoadReferenceable> Referenceables()
    {
        foreach (var map in Find.Maps)
        {
            foreach (var thing in map.listerThings.AllThings)
            {
                yield return thing;
                if (thing is Pawn pawn)
                {
                    foreach (var gear in PawnGear(pawn))
                        yield return gear;
                    // What a job may name: the pawn's abilities and verbs (a visitor's orders refer to them).
                    if (pawn.abilities != null)
                        foreach (var ability in pawn.abilities.AllAbilitiesForReading)
                        {
                            yield return ability;
                            if (ability.verb != null)
                                yield return ability.verb;
                        }
                    if (pawn.verbTracker != null)
                        foreach (var verb in pawn.verbTracker.AllVerbs)
                            yield return verb;
                    if (pawn.equipment?.Primary?.TryGetComp<CompEquippable>() is { } equippable)
                        foreach (var verb in equippable.AllVerbs)
                            yield return verb;
                }
            }
            foreach (var zone in map.zoneManager.AllZones)
                yield return zone;
            foreach (var area in map.areaManager.AllAreas)
                yield return area;
            foreach (var lord in map.lordManager.lords)
                yield return lord;
            foreach (var group in map.storageGroups.StorageGroupsForReading)
                yield return group;
        }

        foreach (var pawn in Find.WorldPawns.AllPawnsAliveOrDead)
            yield return pawn;
        foreach (var faction in Find.FactionManager.AllFactionsListForReading)
            yield return faction;
        foreach (var ideo in Find.IdeoManager.IdeosListForReading)
        {
            yield return ideo;
            foreach (var precept in ideo.PreceptsListForReading)
                yield return precept;
        }
        foreach (var tale in Find.TaleManager.AllTalesListForReading)
            yield return tale;
        foreach (var quest in Find.QuestManager.QuestsListForReading)
            yield return quest;
        foreach (var worldObject in Find.WorldObjects.AllWorldObjects)
            yield return worldObject;
        foreach (var policy in Current.Game.outfitDatabase.AllOutfits)
            yield return policy;
        foreach (var policy in Current.Game.drugPolicyDatabase.AllPolicies)
            yield return policy;
        foreach (var policy in Current.Game.foodRestrictionDatabase.AllFoodRestrictions)
            yield return policy;
    }

    public static IEnumerable<Thing> PawnGear(Pawn pawn)
    {
        if (pawn.apparel != null)
            foreach (var apparel in pawn.apparel.WornApparel)
                yield return apparel;
        if (pawn.equipment != null)
            foreach (var equipment in pawn.equipment.AllEquipmentListForReading)
                yield return equipment;
        if (pawn.inventory != null)
            foreach (var item in pawn.inventory.innerContainer)
                yield return item;
        if (pawn.carryTracker != null)
            foreach (var carried in pawn.carryTracker.innerContainer)
                yield return carried;
    }
}
