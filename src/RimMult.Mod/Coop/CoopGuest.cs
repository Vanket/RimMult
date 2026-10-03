using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml;
using HarmonyLib;
using RimMult.ClientCore;
using RimMult.Shared.Coop;
using RimMult.Shared.Time;
using RimWorld;
using UnityEngine;
using Verse;

namespace RimMult.Coop;

/// <summary>
/// Co-op, guest side: loads the host's game, then keeps the copy in step with what the host streams. The copy never
/// simulates (its ticks are skipped, see <see cref="Patches.CoopTickPatch"/>); it only shows the host's game and turns
/// this player's orders into <see cref="CoopCommand"/>s.
/// </summary>
internal static class CoopGuest
{
    public enum Phase
    {
        None,

        /// <summary>Asked the host for its game, waiting for it.</summary>
        Requested,

        /// <summary>Loading the host's game.</summary>
        Loading,

        /// <summary>Playing the host's colony.</summary>
        Active,
    }

    private const string SaveName = "RimMult_CoopGuest";

    /// <summary>A load that ended in the main menu after this long went wrong (bad save, missing mods).</summary>
    private const float LoadFailAfter = 5f;

    private static readonly AccessTools.FieldRef<TickManager, int> TicksGame = AccessTools.FieldRefAccess<TickManager, int>("ticksGameInt");
    private static readonly Action<ZoneManager> UpdateZoneLinks = AccessTools.MethodDelegate<Action<ZoneManager>>(AccessTools.Method(typeof(ZoneManager), "UpdateZoneManagerLinks"));
    private static readonly Action<ZoneManager> RebuildZoneGrid = AccessTools.MethodDelegate<Action<ZoneManager>>(AccessTools.Method(typeof(ZoneManager), "RebuildZoneGrid"));

    /// <summary>State batches received and not applied yet; merged and applied once per frame.</summary>
    private static readonly List<byte[]> Pending = new();

    /// <summary>Latest known position per pawn (map id → pawn id → position), applied once per frame.</summary>
    private static readonly Dictionary<int, Dictionary<int, PawnPosition>> PendingPositions = new();
    private static int _positionsTick = -1;

    /// <summary>
    /// Fresh copies of pawns held back while a right-click menu is open: swapping a pawn under an open menu breaks the
    /// menu (and with it the whole interface). Applied as soon as the menu closes.
    /// </summary>
    private static readonly Dictionary<int, Dictionary<int, XmlNode>> Deferred = new();
    private static readonly HashSet<Thing> Expected = new();
    private static readonly List<Thing> Strays = new();
    private static Game? _game;
    private static Game? _gameBeforeLoad;
    private static float _loadStarted;
    private static TimeSpeed _applied;
    private static bool _hasApplied;

    public static Phase State { get; private set; }

    /// <summary>Playing the host's colony right now: orders go to the host, the local copy doesn't tick.</summary>
    public static bool Active => State == Phase.Active && Current.ProgramState == ProgramState.Playing && Current.Game == _game;

    /// <summary>True while a batch from the host is applied: the guest's own order patches stand aside.</summary>
    public static bool Applying { get; private set; }

    /// <summary>The speed this guest voted for.</summary>
    public static GameSpeed? MyVote { get; private set; }

    /// <summary>A guest of a co-op server (as opposed to its host, or a separate-colonies session).</summary>
    public static bool IsGuest(ClientSession? session) => session is { Mode: GameMode.Coop } && !Multiplayer.IsHosting;

    public static void Attach(ClientSession session) => session.CoopReceived += OnCoop;

    public static void Reset()
    {
        State = Phase.None;
        Pending.Clear();
        PendingPositions.Clear();
        Deferred.Clear();
        _positionsTick = -1;
        ScribeMemory.ResetCache();
        CoopVisuals.Reset();
        Expected.Clear();
        Strays.Clear();
        _game = null;
        _hasApplied = false;
        MyVote = null;
    }

    /// <summary>The session ended: the copy stops following the host; offer the main menu if it was being played.</summary>
    public static void Leave(string reason)
    {
        var wasActive = Active;
        Reset();
        if (wasActive)
            OfferMainMenu(reason);
    }

    /// <summary>Gives up waiting for the host's game.</summary>
    public static void CancelJoin(ClientSession session)
    {
        if (State != Phase.Requested)
            return;
        State = Phase.None;
        session.LeaveWorld();
    }

    /// <summary>Asks the host for its game ("join the host's colony" in the lobby).</summary>
    public static void RequestJoin(ClientSession session)
    {
        if (State != Phase.None)
            return;
        Pending.Clear();
        State = Phase.Requested;
        session.SendCoop(CoopChannel.JoinRequest, Array.Empty<byte>());
    }

    public static void Update(ClientSession? session)
    {
        if (State == Phase.None)
            return;

        if (session == null || session.State == ClientState.Disconnected)
        {
            Leave(session?.DisconnectReason ?? "");
            return;
        }

        switch (State)
        {
            case Phase.Loading:
                UpdateLoading(session);
                break;
            case Phase.Active when !Active:
                // The guest left the host's colony on their own (main menu, another save).
                Reset();
                session.LeaveWorld();
                break;
            case Phase.Active:
                UpdateSpeed(session);
                ApplyPending();
                ApplyPositions();
                break;
        }
    }

    private static void OnCoop(int sender, CoopChannel channel, byte[] data)
    {
        if (Multiplayer.IsHosting || Multiplayer.Session is not { } session)
            return;

        switch (channel)
        {
            case CoopChannel.NotReady when State == Phase.Requested:
                State = Phase.None;
                session.LeaveWorld(); // releases the server's pause hold
                Messages.Message("RimMult.CoopHostNotReady".Translate(), MessageTypeDefOf.RejectInput, historical: false);
                break;
            case CoopChannel.Game when State == Phase.Requested:
                Load(session, data);
                break;
            case CoopChannel.State when State == Phase.Loading || Active:
                // Applied together once per frame: a slow machine catches up instead of falling further behind.
                Pending.Add(data);
                break;
            case CoopChannel.Positions when State == Phase.Loading || Active:
                QueuePositions(data);
                break;
        }
    }

    private static void Load(ClientSession session, byte[] data)
    {
        try
        {
            File.WriteAllBytes(GenFilePaths.FilePathForSavedGame(SaveName), CoopHost.Decompress(data));
        }
        catch (Exception e)
        {
            Log.Error($"[RimMult] Co-op: could not store the host's game: {e}");
            State = Phase.None;
            session.LeaveWorld();
            return;
        }

        State = Phase.Loading;
        _gameBeforeLoad = Current.Game;
        _loadStarted = Time.realtimeSinceStartup;
        Find.WindowStack.TryRemove(typeof(UI.Dialog_Multiplayer), doCloseSound: false);
        // Straight to loading: the host's mods are this game's mods (the server checked), no version prompts.
        GameDataSaveLoader.LoadGame(SaveName);
    }

    private static void UpdateLoading(ClientSession session)
    {
        if (LongEventHandler.AnyEventNowOrWaiting)
            return;

        if (Current.ProgramState == ProgramState.Playing && Current.Game != null && Current.Game != _gameBeforeLoad)
        {
            _game = Current.Game;
            _gameBeforeLoad = null;
            State = Phase.Active;
            _hasApplied = false;
            MyVote = null;
            DeleteSave();
            ScribeMemory.ResetCache();
            session.SendCoop(CoopChannel.Ready, Array.Empty<byte>());
            ApplyPending();
            ApplyPositions();
            Messages.Message("RimMult.CoopJoined".Translate(session.NameOf(HostId(session))), MessageTypeDefOf.PositiveEvent, historical: false);
        }
        else if (Current.ProgramState == ProgramState.Entry && Time.realtimeSinceStartup - _loadStarted > LoadFailAfter)
        {
            DeleteSave();
            Reset();
            session.LeaveWorld();
            Messages.Message("RimMult.CoopLoadFailed".Translate(), MessageTypeDefOf.RejectInput, historical: false);
        }
    }

    private static int HostId(ClientSession session) => session.Players.FirstOrDefault(p => p.IsHost)?.Id ?? -1;

    private static void DeleteSave()
    {
        try
        {
            var path = GenFilePaths.FilePathForSavedGame(SaveName);
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (IOException)
        {
            // A leftover copy in the saves folder is harmless.
        }
    }

    private static void OfferMainMenu(string reason)
    {
        if (Current.ProgramState != ProgramState.Playing)
            return;
        if (reason.NullOrEmpty())
            reason = "RimMult.CoopConnectionLost".Translate();
        Find.WindowStack.Add(new Dialog_MessageBox(
            "RimMult.CoopDisconnected".Translate(reason),
            "RimMult.CoopToMainMenu".Translate(),
            GenScene.GoToMainMenu,
            "RimMult.CoopKeepCopy".Translate(),
            null));
    }

    /// <summary>The speed buttons are votes, and the copy runs (without ticking) at the shared speed so pawns glide.</summary>
    private static void UpdateSpeed(ClientSession session)
    {
        var tickManager = Find.TickManager;
        var grant = session.LastGrant;

        var observed = tickManager.CurTimeSpeed;
        if (_hasApplied && observed != _applied)
        {
            MyVote = (GameSpeed)(byte)observed;
            session.VoteSpeed(MyVote);
        }
        if (MyVote == GameSpeed.Paused && grant != null && grant.Speed != GameSpeed.Paused)
            MyVote = null;

        var target = (TimeSpeed)(byte)(grant?.Speed ?? GameSpeed.Paused);
        if (tickManager.CurTimeSpeed != target)
            tickManager.CurTimeSpeed = target;
        _applied = target;
        _hasApplied = true;
    }

    // ---------- applying the host's changes ----------

    /// <summary>A thing spawned while a batch is applied that the batch didn't bring (e.g. dropped by a replaced pawn).</summary>
    public static void NotifySpawned(Thing thing)
    {
        if (Applying && !Expected.Contains(thing))
            Strays.Add(thing);
    }

    /// <summary>Everything one map got from several batches, reduced to the latest state.</summary>
    private sealed class MergedDelta
    {
        public readonly HashSet<int> Despawned = new();
        public readonly Dictionary<int, XmlNode> Fragments = new();
        public readonly Dictionary<int, ThingPatch> Patches = new();
        public List<DesignationEntry>? Designations;
        public string? Grids;
        public string? Zones;
        public readonly List<CoopShot> Shots = new();
    }

    private static void QueuePositions(byte[] data)
    {
        PositionsFrame frame;
        try
        {
            frame = PositionsFrame.Decode(data);
        }
        catch (Exception e)
        {
            Log.WarningOnce($"[RimMult] Co-op: bad positions from the host: {e.Message}", 0x434F5053);
            return;
        }
        if (frame.Tick > _positionsTick)
            _positionsTick = frame.Tick;
        foreach (var (mapId, positions) in frame.Maps)
        {
            if (!PendingPositions.TryGetValue(mapId, out var byPawn))
                PendingPositions[mapId] = byPawn = new Dictionary<int, PawnPosition>();
            foreach (var position in positions)
                byPawn[position.ThingId] = position;
        }
    }

    /// <summary>Moves pawns to where the host has them. Runs every frame; cheap.</summary>
    private static void ApplyPositions()
    {
        if (PendingPositions.Count == 0 || !Active)
            return;

        var tickManager = Find.TickManager;
        if (_positionsTick > tickManager.TicksGame)
            TicksGame(tickManager) = _positionsTick;

        // Paused (or too fast to tween): snap. Otherwise the pawn glides to its new cell.
        var snap = tickManager.Paused || tickManager.TickRateMultiplier >= 5f;
        Applying = true;
        try
        {
            foreach (var pair in PendingPositions)
            {
                var map = Find.Maps.FirstOrDefault(m => m.uniqueID == pair.Key);
                if (map == null)
                    continue;
                foreach (var pawn in map.mapPawns.AllPawnsSpawned.ToList())
                {
                    if (!pair.Value.TryGetValue(pawn.thingIDNumber, out var position))
                        continue;
                    var cell = new IntVec3(position.X, 0, position.Z);
                    if (cell.InBounds(map) && pawn.Position != cell)
                    {
                        pawn.Position = cell;
                        pawn.Notify_Teleported(endCurrentJob: false, resetTweenedPos: snap);
                    }
                    // Drawn where the host draws it, gliding there (see CoopVisuals): no snapping from cell to cell.
                    CoopVisuals.OnPosition(pawn.thingIDNumber, new Vector3(position.DrawX / 100f, 0f, position.DrawZ / 100f));
                    var rotation = new Rot4(position.Rotation & 3);
                    if (pawn.Rotation != rotation)
                        pawn.Rotation = rotation;
                }
            }
        }
        catch (Exception e)
        {
            Log.ErrorOnce($"[RimMult] Co-op: could not move pawns: {e}", 0x434F4D56);
        }
        finally
        {
            Applying = false;
            PendingPositions.Clear();
        }
    }

    /// <summary>Merges all batches received since the last frame and applies the result once.</summary>
    private static void ApplyPending()
    {
        var menuOpen = Find.WindowStack.IsOpen<FloatMenu>();
        if ((Pending.Count == 0 && (Deferred.Count == 0 || menuOpen)) || !ScribeMemory.Idle)
            return;

        var merged = new Dictionary<int, MergedDelta>();
        var tick = -1;
        foreach (var data in Pending)
        {
            CoopBatch batch;
            try
            {
                batch = CoopBatch.Decode(CoopHost.Decompress(data));
            }
            catch (Exception e)
            {
                Log.Warning($"[RimMult] Co-op: bad state from the host: {e.Message}");
                continue;
            }
            tick = Math.Max(tick, batch.Tick);
            // Relations, research and letters don't depend on the maps: applied in order as they come.
            CoopWorldSync.Apply(batch.World);
            foreach (var delta in batch.Maps)
            {
                if (!merged.TryGetValue(delta.MapId, out var m))
                    merged[delta.MapId] = m = new MergedDelta();
                foreach (var id in delta.Despawned)
                {
                    m.Fragments.Remove(id);
                    m.Patches.Remove(id);
                    m.Despawned.Add(id);
                }
                foreach (var xml in delta.Things)
                {
                    XmlNode? li;
                    try
                    {
                        li = ScribeMemory.Parse(xml)["li"];
                    }
                    catch (XmlException)
                    {
                        continue;
                    }
                    if (li == null || ScribeMemory.FragmentThingId(li) is not { } id)
                        continue;
                    // A newer full copy wins over an older removal or patch.
                    m.Despawned.Remove(id);
                    m.Patches.Remove(id);
                    m.Fragments[id] = li;
                }
                foreach (var patch in delta.Patches)
                    m.Patches[patch.ThingId] = patch;
                m.Designations = delta.Designations ?? m.Designations;
                m.Grids = delta.Grids ?? m.Grids;
                m.Zones = delta.Zones ?? m.Zones;
                m.Shots.AddRange(delta.Shots);
            }
        }
        Pending.Clear();

        // Pawn copies held back for a menu: older than anything that just arrived, so they only fill gaps.
        if (!menuOpen)
        {
            foreach (var mapPair in Deferred)
            {
                if (!merged.TryGetValue(mapPair.Key, out var m))
                    merged[mapPair.Key] = m = new MergedDelta();
                foreach (var pawnPair in mapPair.Value)
                {
                    if (!m.Fragments.ContainsKey(pawnPair.Key) && !m.Despawned.Contains(pawnPair.Key))
                        m.Fragments[pawnPair.Key] = pawnPair.Value;
                }
            }
            Deferred.Clear();
        }

        Applying = true;
        try
        {
            if (tick > Find.TickManager.TicksGame)
                TicksGame(Find.TickManager) = tick;
            foreach (var pair in merged)
            {
                var map = Find.Maps.FirstOrDefault(m => m.uniqueID == pair.Key);
                if (map == null)
                    continue; // a map the host made after this guest joined
                try
                {
                    ApplyMerged(map, pair.Value);
                }
                catch (Exception e)
                {
                    Log.ErrorOnce($"[RimMult] Co-op: could not apply the host's changes: {e}", 0x434F4750);
                }
            }

            foreach (var stray in Strays.Where(s => s.Spawned && !Expected.Contains(s)))
                stray.DeSpawn(DestroyMode.Vanish);
        }
        finally
        {
            Expected.Clear();
            Strays.Clear();
            Applying = false;
        }
    }

    private static void ApplyMerged(Map map, MergedDelta delta)
    {
        var byId = CoopCommands.ThingsById(map);

        foreach (var id in delta.Despawned)
        {
            if (byId.TryGetValue(id, out var gone) && gone.Spawned)
                gone.DeSpawn(DestroyMode.Vanish);
        }

        foreach (var shot in delta.Shots)
            CoopVisuals.OnShot(map, shot);

        if (Find.WindowStack.IsOpen<FloatMenu>())
        {
            foreach (var id in delta.Fragments.Keys.Where(id => byId.TryGetValue(id, out var t) && t is Pawn).ToList())
            {
                if (!Deferred.TryGetValue(map.uniqueID, out var held))
                    Deferred[map.uniqueID] = held = new Dictionary<int, XmlNode>();
                held[id] = delta.Fragments[id];
                delta.Fragments.Remove(id);
            }
        }

        if (delta.Fragments.Count > 0)
        {
            ApplyThings(map, delta.Fragments.Values.ToList(), byId);
            byId = CoopCommands.ThingsById(map);
        }

        foreach (var patch in delta.Patches.Values)
        {
            if (byId.TryGetValue(patch.ThingId, out var thing))
                ApplyPatch(map, thing, patch);
        }

        if (delta.Designations != null)
            ApplyDesignations(map, delta.Designations, byId);
        if (delta.Grids != null)
            ApplyGrids(map, delta.Grids);
        if (delta.Zones != null)
            ApplyZones(map, delta.Zones);
    }

    /// <summary>Small changes in place: no reload, no respawn, only a redraw of that cell when the look changed.</summary>
    private static void ApplyPatch(Map map, Thing thing, ThingPatch patch)
    {
        var redraw = false;
        if (thing.def.useHitPoints && thing.HitPoints != patch.HitPoints)
            thing.HitPoints = patch.HitPoints;
        if (patch.StackCount > 0 && thing.stackCount != patch.StackCount)
        {
            thing.stackCount = patch.StackCount;
            redraw = true;
        }
        if (patch.Growth >= 0f && thing is Plant plant && Math.Abs(plant.Growth - patch.Growth) > 0.0001f)
        {
            plant.Growth = patch.Growth;
            redraw = true;
        }
        if (patch.WorkDone >= 0f && thing is Frame frame)
        {
            frame.workDone = patch.WorkDone;
            redraw = true;
        }
        if (patch.Forbidden != ThingPatch.NoForbid && thing.TryGetComp<CompForbiddable>() is { } forbiddable
                                                  && forbiddable.Forbidden != (patch.Forbidden == 1))
            forbiddable.Forbidden = patch.Forbidden == 1;
        if (redraw && thing.Spawned)
            map.mapDrawer.MapMeshDirty(thing.Position, MapMeshFlagDefOf.Things);
    }

    /// <summary>Spawns new things and swaps changed ones for their freshly loaded state.</summary>
    private static void ApplyThings(Map map, List<XmlNode> fragments, Dictionary<int, Thing> byId)
    {
        var nodes = new List<XmlNode>();
        var replacedIds = new HashSet<string>();
        foreach (var li in fragments)
        {
            if (ScribeMemory.FragmentThingId(li) is not { } id || ScribeMemory.FragmentLoadId(li) is not { } loadId)
                continue;

            nodes.Add(li);
            replacedIds.Add(loadId);
            // Things inside the fragment (a corpse's pawn, gear, a minified building, what is carried) are loaded
            // anew too: the old objects with those ids must not be offered as references, or loading fails.
            foreach (XmlNode inner in li.SelectNodes(".//id")!)
            {
                if (inner.InnerText.Length > 0)
                    replacedIds.Add("Thing_" + inner.InnerText);
            }
            // A pawn comes with its gear: the old pawn's gear gives way too.
            if (byId.TryGetValue(id, out var old) && old is Pawn oldPawn)
                foreach (var gear in ScribeMemory.PawnGear(oldPawn))
                    replacedIds.Add(gear.GetUniqueLoadID());
        }
        if (nodes.Count == 0)
            return;

        List<Thing> loaded;
        try
        {
            loaded = ScribeMemory.LoadThings(nodes, replacedIds);
        }
        catch (Exception e)
        {
            Log.WarningOnce($"[RimMult] Co-op: could not load things from the host: {e.Message}", 0x434F4C44);
            return;
        }

        var anyPawn = false;
        foreach (var thing in loaded)
        {
            Expected.Add(thing);
            var position = thing.Position;
            if (!position.InBounds(map))
                continue;

            var selected = false;
            List<DesignationDef>? designations = null;
            if (byId.TryGetValue(thing.thingIDNumber, out var old))
            {
                selected = Find.Selector.IsSelected(old);
                if (old.Spawned)
                {
                    // Designations on the old object (chop this tree) carry over; the host only re-sends them when they change.
                    designations = map.designationManager.AllDesignationsOn(old).Select(d => d.def).ToList();
                    old.DeSpawn(DestroyMode.Vanish);
                }
            }

            try
            {
                GenSpawn.Spawn(thing, position, map, thing.Rotation, WipeMode.Vanish, respawningAfterLoad: true);
            }
            catch (Exception e)
            {
                Log.WarningOnce($"[RimMult] Co-op: could not place {thing}: {e.Message}", thing.thingIDNumber ^ 0x434F5350);
                continue;
            }

            ScribeMemory.Remember(thing);
            if (designations != null)
                foreach (var def in designations)
                    map.designationManager.AddDesignation(new Designation(thing, def));
            if (selected)
                Find.Selector.Select(thing, playSound: false, forceDesignatorDeselect: false);
            anyPawn |= thing is Pawn;
        }

        if (anyPawn)
            Find.ColonistBar?.MarkColonistsDirty();
    }

    private static void ApplyDesignations(Map map, List<DesignationEntry> entries, Dictionary<int, Thing> byId)
    {
        var manager = map.designationManager;
        foreach (var designation in manager.AllDesignations.ToList())
            manager.RemoveDesignation(designation);

        foreach (var entry in entries)
        {
            var def = DefDatabase<DesignationDef>.GetNamedSilentFail(entry.DefName);
            if (def == null)
                continue;
            LocalTargetInfo target;
            if (entry.ThingId >= 0)
            {
                if (!byId.TryGetValue(entry.ThingId, out var thing))
                    continue;
                target = thing;
            }
            else
            {
                var cell = new IntVec3(entry.X, 0, entry.Z);
                if (!cell.InBounds(map))
                    continue;
                target = cell;
            }
            manager.AddDesignation(new Designation(target, def));
        }
    }

    /// <summary>Loads terrain, roofs and fog into the existing grids and redraws the cells that changed.</summary>
    private static void ApplyGrids(Map map, string xml)
    {
        var cells = map.cellIndices.NumGridCells;
        var terrain = new TerrainDef[cells];
        var roofs = new RoofDef[cells];
        var fog = new bool[cells];
        for (var i = 0; i < cells; i++)
        {
            terrain[i] = map.terrainGrid.TerrainAt(i);
            roofs[i] = map.roofGrid.RoofAt(i);
            fog[i] = map.fogGrid.IsFogged(i);
        }

        ScribeMemory.Load(ScribeMemory.Parse(xml), new HashSet<string>(), () =>
        {
            map.terrainGrid.ExposeData();
            map.roofGrid.ExposeData();
            map.fogGrid.ExposeData();
        });

        for (var i = 0; i < cells; i++)
        {
            ulong flags = 0;
            if (map.terrainGrid.TerrainAt(i) != terrain[i])
                flags |= (ulong)MapMeshFlagDefOf.Terrain;
            if (map.roofGrid.RoofAt(i) != roofs[i])
                flags |= (ulong)MapMeshFlagDefOf.Roofs;
            if (map.fogGrid.IsFogged(i) != fog[i])
                flags |= (ulong)MapMeshFlagDefOf.FogOfWar | MapMeshFlagDefOf.Things;
            if (flags != 0)
                map.mapDrawer.MapMeshDirty(map.cellIndices.IndexToCell(i), flags);
        }
    }

    /// <summary>Swaps the map's zones for the host's (same ids), keeping a selected zone selected.</summary>
    private static void ApplyZones(Map map, string xml)
    {
        var old = map.zoneManager;
        var selected = Find.Selector.SelectedZone is { } zone && zone.zoneManager == old ? zone.GetUniqueLoadID() : null;
        var replacedIds = new HashSet<string>(old.AllZones.Select(z => z.GetUniqueLoadID()));
        foreach (var gone in old.AllZones.ToList())
            old.DeregisterZone(gone);

        var fresh = new ZoneManager(map);
        map.zoneManager = fresh;
        void Link()
        {
            UpdateZoneLinks(fresh);
            RebuildZoneGrid(fresh);
        }
        ScribeMemory.Load(ScribeMemory.Parse(xml), replacedIds, fresh.ExposeData, Link);
        Link();
        // What registering a zone does at runtime (stockpiles become haul destinations), mirroring DeregisterZone above.
        foreach (var added in fresh.AllZones)
            added.PostRegister();

        if (selected != null && fresh.AllZones.FirstOrDefault(z => z.GetUniqueLoadID() == selected) is { } again)
            Find.Selector.Select(again, playSound: false, forceDesignatorDeselect: false);
        map.mapDrawer.WholeMapChanged(MapMeshFlagDefOf.Zone);
    }
}
