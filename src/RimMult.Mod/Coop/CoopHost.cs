using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using RimMult.ClientCore;
using RimMult.Shared.Coop;
using RimWorld;
using UnityEngine;
using Verse;

namespace RimMult.Coop;

/// <summary>
/// Co-op, host side: the host's game is the only one that runs. Joining guests get a save of it; then the host
/// streams what changes:
/// <list type="bullet">
/// <item>pawn positions ~30 times a second on their own unreliable channel (nothing big ever waits in front of them);</item>
/// <item>every 0.1 s a state batch: what spawned or vanished, small in-place patches (hit points, stack size, plant
/// growth, construction progress, forbidden), full copies of pawns whose look changed and of things whose saved
/// state changed (any mod's data), designations and zones as soon as they change, and the terrain/roof/fog grids.</item>
/// </list>
/// Everything is change-driven and capped per batch, so a slow guest machine is never flooded.
/// Guests' orders arrive as <see cref="CoopCommand"/>s.
/// </summary>
internal static class CoopHost
{
    private const float PositionsInterval = 1f / 30f;
    private const float PositionsKeepAlive = 0.5f;
    private const int PositionsFrameBytes = 900;
    private const float BatchInterval = 0.1f;
    private const float ZoneCheckInterval = 0.25f;
    private const float ZoneFullInterval = 3f;
    private const float GridInterval = 1f;
    private const float PawnRollInterval = 0.5f;
    private const int ScanPerBatch = 80;
    private const int XmlChecksPerBatch = 6;
    private const int MaxFragmentsPerBatch = 12;
    private const string TransferSaveName = "RimMult_CoopTransfer";

    /// <summary>Guests that asked for the game (playing or loading it): nothing is streamed while there are none.</summary>
    private static readonly HashSet<int> Guests = new();
    private static readonly HashSet<Thing> Spawned = new();
    private static readonly Dictionary<int, List<int>> Despawned = new();
    private static readonly Dictionary<int, List<CoopShot>> Shots = new();

    /// <summary>Things (mostly pawns) waiting for a full copy, oldest first; capped per batch.</summary>
    private static readonly List<Thing> Pending = new();

    private static readonly Dictionary<int, int> CheapSignature = new();
    private static readonly Dictionary<int, int> XmlHash = new();
    private static readonly Dictionary<int, int> PawnSignature = new();
    private static readonly Dictionary<int, int> ThingCursor = new();
    private static readonly Dictionary<int, int> XmlCursor = new();
    private static readonly Dictionary<int, int> PawnCursor = new();
    private static readonly Dictionary<int, int> DesignationHash = new();
    private static readonly Dictionary<int, int> ZoneCheapHash = new();
    private static readonly Dictionary<int, int> ZoneHash = new();
    private static readonly Dictionary<int, int> GridHash = new();
    private static float _lastPositions;
    private static float _lastPositionsSent;
    private static byte[] _lastPositionsData = Array.Empty<byte>();
    private static float _lastBatch;
    private static float _lastZoneCheck;
    private static float _lastZoneFull;
    private static float _lastGrid;
    private static float _lastPawnRoll;

    /// <summary>Hosting a co-op game with the colony loaded: changes are recorded and streamed.</summary>
    public static bool Active =>
        Multiplayer.IsHosting
        && Multiplayer.Session is { State: ClientState.Connected, Mode: GameMode.Coop }
        && Current.ProgramState == ProgramState.Playing
        && !LongEventHandler.AnyEventNowOrWaiting;

    public static void Attach(ClientSession session) => session.CoopReceived += OnCoop;

    public static void Reset()
    {
        Guests.Clear();
        Spawned.Clear();
        Despawned.Clear();
        Pending.Clear();
        CheapSignature.Clear();
        XmlHash.Clear();
        PawnSignature.Clear();
        ThingCursor.Clear();
        XmlCursor.Clear();
        PawnCursor.Clear();
        ForgetSentState();
    }

    /// <summary>Designations, zones and grids go out in full again (a guest just loaded the game).</summary>
    private static void ForgetSentState()
    {
        DesignationHash.Clear();
        ZoneCheapHash.Clear();
        ZoneHash.Clear();
        GridHash.Clear();
        _lastPositionsData = Array.Empty<byte>();
    }

    public static void NotifySpawned(Thing thing)
    {
        // Projectiles go out as shots (guests only draw them), not as things.
        if (Active && thing is not Projectile)
            Spawned.Add(thing);
    }

    public static void NotifyDespawned(Thing thing)
    {
        if (!Active || thing.Map == null || thing.thingIDNumber < 0 || thing is Projectile)
            return;
        Spawned.Remove(thing);
        Pending.Remove(thing);
        if (!Despawned.TryGetValue(thing.Map.uniqueID, out var list))
            Despawned[thing.Map.uniqueID] = list = new List<int>();
        list.Add(thing.thingIDNumber);
    }

    /// <summary>A shot was fired on the host: guests draw the projectile flying and play the weapon's sound.</summary>
    public static void NotifyShot(Projectile projectile, Vector3 origin, Vector3 destination, int ticks, SoundDef? sound)
    {
        if (!Active || Guests.Count == 0 || projectile.Map == null)
            return;
        if (!Shots.TryGetValue(projectile.Map.uniqueID, out var list))
            Shots[projectile.Map.uniqueID] = list = new List<CoopShot>();
        if (list.Count < 200)
            list.Add(new CoopShot(projectile.def.defName, origin.x, origin.z, destination.x, destination.z, ticks, sound?.defName ?? ""));
    }

    /// <summary>Something whose state a click just changed (a guest's order, the host's button): sent in full right away.</summary>
    public static void Touch(Thing thing)
    {
        if (Active && Guests.Count > 0 && thing.Spawned && thing.thingIDNumber >= 0 && thing is not Projectile)
            Queue(thing);
    }

    public static void Update(ClientSession session)
    {
        Guests.RemoveWhere(id => !session.Players.Any(p => p.Id == id));
        if (!Active || Guests.Count == 0 || !ScribeMemory.Idle)
        {
            Spawned.Clear();
            Despawned.Clear();
            Shots.Clear();
            Pending.Clear();
            return;
        }

        var now = Time.realtimeSinceStartup;
        if (now - _lastPositions >= PositionsInterval)
        {
            _lastPositions = now;
            SendPositions(session, now);
        }

        if (now - _lastBatch < BatchInterval)
            return;
        _lastBatch = now;

        var checkZones = now - _lastZoneCheck >= ZoneCheckInterval;
        if (checkZones)
            _lastZoneCheck = now;
        var fullZones = now - _lastZoneFull >= ZoneFullInterval;
        if (fullZones)
            _lastZoneFull = now;
        var grids = now - _lastGrid >= GridInterval;
        if (grids)
            _lastGrid = now;
        var rollPawn = now - _lastPawnRoll >= PawnRollInterval;
        if (rollPawn)
            _lastPawnRoll = now;

        var batch = new CoopBatch { Tick = Find.TickManager.TicksGame };
        var budget = MaxFragmentsPerBatch;
        foreach (var map in Find.Maps)
        {
            var delta = BuildDelta(map, checkZones, fullZones, grids, rollPawn, ref budget);
            if (!IsEmpty(delta))
                batch.Maps.Add(delta);
        }
        Spawned.Clear();
        Despawned.Clear();
        Shots.Clear();
        if (batch.Maps.Count > 0)
            session.SendCoop(CoopChannel.State, Compress(batch.Encode()));
    }

    private static bool IsEmpty(MapDelta delta) =>
        delta.Despawned.Count == 0 && delta.Things.Count == 0 && delta.Patches.Count == 0 && delta.Shots.Count == 0
        && delta.Designations == null && delta.Grids == null && delta.Zones == null;

    /// <summary>Where every pawn stands; sent when something moved (and now and then anyway, the channel may drop).</summary>
    private static void SendPositions(ClientSession session, float now)
    {
        var maps = Find.Maps.Select(map => (map.uniqueID, map.mapPawns.AllPawnsSpawned
                .Where(p => p.thingIDNumber >= 0)
                .Select(p =>
                {
                    // Where the pawn is drawn, between cells while it walks: guests glide it along the same path.
                    var draw = p.DrawPos;
                    return new PawnPosition(p.thingIDNumber, p.Position.x, p.Position.z, (byte)p.Rotation.AsInt,
                        Mathf.RoundToInt(draw.x * 100f), Mathf.RoundToInt(draw.z * 100f));
                })
                .ToList()))
            .ToList();
        var key = new PositionsFrame { Maps = maps }.Encode();
        if (key.SequenceEqual(_lastPositionsData) && now - _lastPositionsSent < PositionsKeepAlive)
            return;
        _lastPositionsData = key;
        _lastPositionsSent = now;
        foreach (var frame in PositionsFrame.Split(Find.TickManager.TicksGame, maps, PositionsFrameBytes))
            session.SendCoop(CoopChannel.Positions, frame);
    }

    private static MapDelta BuildDelta(Map map, bool checkZones, bool fullZones, bool grids, bool rollPawn, ref int budget)
    {
        var delta = new MapDelta { MapId = map.uniqueID };

        if (Despawned.TryGetValue(map.uniqueID, out var gone))
            delta.Despawned.AddRange(gone);
        if (Shots.TryGetValue(map.uniqueID, out var shots))
            delta.Shots.AddRange(shots);

        // New things always go out right away (they are small and the guest must see them).
        foreach (var thing in Spawned.Where(t => t.Spawned && t.Map == map))
            AddFragment(delta, thing);

        // Pawns whose look changed (carrying, drafted, downed, gear…) get a fresh copy; one more now and then for
        // needs and health.
        var pawns = map.mapPawns.AllPawnsSpawned;
        foreach (var pawn in pawns)
        {
            var signature = Signature(pawn);
            if (PawnSignature.TryGetValue(pawn.thingIDNumber, out var old) && old != signature)
                Queue(pawn);
            PawnSignature[pawn.thingIDNumber] = signature;
        }
        if (rollPawn && pawns.Count > 0)
        {
            var cursor = PawnCursor.TryGetValue(map.uniqueID, out var c) ? (c + 1) % pawns.Count : 0;
            PawnCursor[map.uniqueID] = cursor;
            Queue(pawns[cursor]);
        }

        var things = map.listerThings.AllThings;
        if (things.Count > 0)
        {
            // Quick look at many things: small changes become patches.
            var cursor = ThingCursor.TryGetValue(map.uniqueID, out var c) ? c : 0;
            for (var i = 0; i < ScanPerBatch && i < things.Count; i++)
            {
                cursor = (cursor + 1) % things.Count;
                var thing = things[cursor];
                if (thing is Pawn or Projectile || thing.thingIDNumber < 0)
                    continue;
                var signature = Signature(thing);
                if (CheapSignature.TryGetValue(thing.thingIDNumber, out var old) && old != signature)
                    delta.Patches.Add(PatchOf(thing));
                CheapSignature[thing.thingIDNumber] = signature;
            }
            ThingCursor[map.uniqueID] = cursor;

            // Slow, thorough look at a few: anything else in the saved state (fuel, power, a mod's data) means a full copy.
            var xmlCursor = XmlCursor.TryGetValue(map.uniqueID, out var x) ? x : 0;
            for (var i = 0; i < XmlChecksPerBatch && i < things.Count; i++)
            {
                xmlCursor = (xmlCursor + 1) % things.Count;
                var thing = things[xmlCursor];
                if (thing is Pawn or Plant or Projectile || thing.thingIDNumber < 0)
                    continue;
                CheckXml(thing);
            }
            XmlCursor[map.uniqueID] = xmlCursor;
        }

        // What the host has selected is looked at every batch: their clicks (medical bed, forbid, settings) show at once.
        foreach (var selected in Find.Selector.SelectedObjects.OfType<Thing>())
        {
            if (selected.Map == map && selected.Spawned && selected is not Pawn && selected.thingIDNumber >= 0)
                CheckXml(selected);
        }

        foreach (var thing in Pending.Where(t => t.Map == map).ToList())
        {
            if (budget <= 0)
                break;
            Pending.Remove(thing);
            if (thing.Spawned && !delta.Despawned.Contains(thing.thingIDNumber))
            {
                AddFragment(delta, thing);
                budget--;
            }
        }

        // Designations: a cheap hash every batch, so a guest's "mine here" shows up at once.
        var designationHash = DesignationsHash(map);
        if (!DesignationHash.TryGetValue(map.uniqueID, out var oldDesignations) || oldDesignations != designationHash)
        {
            DesignationHash[map.uniqueID] = designationHash;
            delta.Designations = map.designationManager.AllDesignations
                .Select(d => new DesignationEntry(d.def.defName, d.target.HasThing ? d.target.Thing.thingIDNumber : -1, d.target.Cell.x, d.target.Cell.z))
                .ToList();
        }

        // Zones: cheap check often (new stockpile, cells added), full comparison now and then (settings).
        if (checkZones)
        {
            var cheap = ZonesHash(map);
            var cheapChanged = !ZoneCheapHash.TryGetValue(map.uniqueID, out var oldCheap) || oldCheap != cheap;
            ZoneCheapHash[map.uniqueID] = cheap;
            if (cheapChanged || fullZones)
            {
                var zones = ScribeMemory.Save(() => map.zoneManager.ExposeData());
                var zoneHash = Hash(zones);
                if (!ZoneHash.TryGetValue(map.uniqueID, out var oldZones) || oldZones != zoneHash)
                {
                    ZoneHash[map.uniqueID] = zoneHash;
                    delta.Zones = zones;
                }
            }
        }

        if (grids)
        {
            var gridXml = ScribeMemory.Save(() =>
            {
                map.terrainGrid.ExposeData();
                map.roofGrid.ExposeData();
                map.fogGrid.ExposeData();
            });
            var gridHash = Hash(gridXml);
            if (!GridHash.TryGetValue(map.uniqueID, out var oldGrid) || oldGrid != gridHash)
            {
                GridHash[map.uniqueID] = gridHash;
                delta.Grids = gridXml;
            }
        }

        return delta;
    }

    private static void CheckXml(Thing thing)
    {
        if (Save(thing) is not { } xml)
            return;
        var hash = Hash(xml);
        if (XmlHash.TryGetValue(thing.thingIDNumber, out var old) && old != hash)
            Queue(thing);
        XmlHash[thing.thingIDNumber] = hash;
    }

    private static void Queue(Thing thing)
    {
        if (!Pending.Contains(thing))
            Pending.Add(thing);
    }

    private static void AddFragment(MapDelta delta, Thing thing)
    {
        // Things without an id (rare, purely visual ones) can't be matched up on the guest's side.
        if (thing.thingIDNumber < 0 || Save(thing) is not { } xml)
            return;
        delta.Things.Add(xml);
        XmlHash[thing.thingIDNumber] = Hash(xml);
        if (thing is Pawn pawn)
            PawnSignature[pawn.thingIDNumber] = Signature(pawn);
        else
            CheapSignature[thing.thingIDNumber] = Signature(thing);
    }

    private static string? Save(Thing thing)
    {
        try
        {
            return ScribeMemory.SaveThing(thing);
        }
        catch (Exception e)
        {
            Log.WarningOnce($"[RimMult] Co-op: could not save {thing}: {e.Message}", thing.thingIDNumber ^ 0x434F4F50);
            return null;
        }
    }

    private static ThingPatch PatchOf(Thing thing) => new(
        thing.thingIDNumber,
        thing.HitPoints,
        thing.stackCount,
        thing is Plant plant ? plant.Growth : -1f,
        thing is Frame frame ? frame.workDone : -1f,
        thing.TryGetComp<CompForbiddable>() is { } forbiddable ? (byte)(forbiddable.Forbidden ? 1 : 0) : ThingPatch.NoForbid);

    /// <summary>What a patch carries, coarsely: plants only count once their growth moved a visible step.</summary>
    private static int Signature(Thing thing)
    {
        unchecked
        {
            var hash = thing.HitPoints;
            hash = hash * 31 + thing.stackCount;
            if (thing is Plant plant)
                hash = hash * 31 + (int)(plant.Growth * 20f);
            if (thing is Frame frame)
                hash = hash * 31 + (int)(frame.workDone / 10f);
            if (thing.TryGetComp<CompForbiddable>() is { } forbiddable)
                hash = hash * 31 + (forbiddable.Forbidden ? 1 : 2);
            return hash;
        }
    }

    /// <summary>What a guest would see change on a pawn without a fresh copy.</summary>
    private static int Signature(Pawn pawn)
    {
        unchecked
        {
            var hash = pawn.carryTracker?.CarriedThing?.thingIDNumber ?? 0;
            hash = hash * 31 + (pawn.Drafted ? 1 : 0);
            hash = hash * 31 + (pawn.Downed ? 1 : 0);
            hash = hash * 31 + (pawn.Dead ? 1 : 0);
            hash = hash * 31 + (int)pawn.GetPosture();
            hash = hash * 31 + (pawn.equipment?.Primary?.thingIDNumber ?? 0);
            hash = hash * 31 + (pawn.mindState?.mentalStateHandler?.CurStateDef?.shortHash ?? 0);
            hash = hash * 31 + (pawn.health?.hediffSet?.hediffs.Count ?? 0);
            if (pawn.apparel != null)
                foreach (var apparel in pawn.apparel.WornApparel)
                    hash = hash * 31 + apparel.thingIDNumber;
            return hash;
        }
    }

    private static int DesignationsHash(Map map)
    {
        unchecked
        {
            var hash = 17;
            foreach (var designation in map.designationManager.AllDesignations)
            {
                hash = hash * 31 + designation.def.shortHash;
                hash = hash * 31 + (designation.target.HasThing ? designation.target.Thing.thingIDNumber : map.cellIndices.CellToIndex(designation.target.Cell));
            }
            return hash;
        }
    }

    private static int ZonesHash(Map map)
    {
        unchecked
        {
            var hash = 17;
            foreach (var zone in map.zoneManager.AllZones)
            {
                hash = hash * 31 + zone.ID;
                hash = hash * 31 + zone.Cells.Count;
                hash = hash * 31 + (zone.label?.GetHashCode() ?? 0);
                foreach (var cell in zone.Cells)
                    hash = hash * 31 + map.cellIndices.CellToIndex(cell);
                if (zone is Zone_Growing growing)
                    hash = hash * 31 + (growing.GetPlantDefToGrow()?.shortHash ?? 0);
            }
            return hash;
        }
    }

    private static int Hash(string text) => text.GetHashCode() ^ text.Length;

    private static void OnCoop(int guestId, CoopChannel channel, byte[] data)
    {
        if (!Multiplayer.IsHosting)
            return;
        var session = Multiplayer.Session!;

        switch (channel)
        {
            case CoopChannel.JoinRequest:
                if (Current.ProgramState != ProgramState.Playing || LongEventHandler.AnyEventNowOrWaiting || !ScribeMemory.Idle)
                {
                    session.SendCoop(CoopChannel.NotReady, Array.Empty<byte>(), guestId);
                    return;
                }
                session.SendCoop(CoopChannel.Game, SaveCurrentGame(), guestId);
                Guests.Add(guestId);
                // Everything after this save reaches the guest as batches; grids, zones and designations go out in full again.
                ForgetSentState();
                Messages.Message("RimMult.CoopGuestJoining".Translate(session.NameOf(guestId)), RimWorld.MessageTypeDefOf.NeutralEvent, historical: false);
                break;
            case CoopChannel.Ready:
                Messages.Message("RimMult.CoopGuestJoined".Translate(session.NameOf(guestId)), RimWorld.MessageTypeDefOf.PositiveEvent, historical: false);
                break;
            case CoopChannel.Command:
                CoopCommands.Execute(guestId, data);
                break;
        }
    }

    /// <summary>The whole game, saved the normal way and gzipped.</summary>
    private static byte[] SaveCurrentGame()
    {
        GameDataSaveLoader.SaveGame(TransferSaveName);
        var path = GenFilePaths.FilePathForSavedGame(TransferSaveName);
        try
        {
            return Compress(File.ReadAllBytes(path));
        }
        finally
        {
            try
            {
                File.Delete(path);
            }
            catch (IOException)
            {
            }
        }
    }

    public static byte[] Compress(byte[] data)
    {
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionMode.Compress))
            gzip.Write(data, 0, data.Length);
        return output.ToArray();
    }

    public static byte[] Decompress(byte[] data)
    {
        using var input = new GZipStream(new MemoryStream(data), CompressionMode.Decompress);
        using var output = new MemoryStream();
        input.CopyTo(output);
        return output.ToArray();
    }
}
