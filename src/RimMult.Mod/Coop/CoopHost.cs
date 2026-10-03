using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using RimMult.ClientCore;
using RimMult.Shared.Coop;
using UnityEngine;
using Verse;

namespace RimMult.Coop;

/// <summary>
/// Co-op, host side: the host's game is the only one that runs. Joining guests get a save of it; then the host
/// streams what changes, ten times a second: where pawns are, what spawned or vanished, a rolling re-send of every
/// thing whose saved state changed (this is what carries construction progress, plant growth, health and any mod's
/// data), the designations, the zones and the terrain/roof/fog grids. Guests' orders arrive as <see cref="CoopCommand"/>s.
/// </summary>
internal static class CoopHost
{
    private const float BatchInterval = 0.1f;
    private const float SlowInterval = 3f;
    private const int ThingsPerBatch = 30;
    private const string TransferSaveName = "RimMult_CoopTransfer";

    /// <summary>Guests that asked for the game (playing or loading it): batches are only made while there are any.</summary>
    private static readonly HashSet<int> Guests = new();
    private static readonly HashSet<Thing> Spawned = new();
    private static readonly Dictionary<int, List<int>> Despawned = new();
    private static readonly Dictionary<int, int> SentHash = new();
    private static readonly Dictionary<int, int> ThingCursor = new();
    private static readonly Dictionary<int, int> PawnCursor = new();
    private static readonly Dictionary<int, int> DesignationHash = new();
    private static readonly Dictionary<int, int> GridHash = new();
    private static readonly Dictionary<int, int> ZoneHash = new();
    private static float _lastBatch;
    private static float _lastSlow;

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
        SentHash.Clear();
        ThingCursor.Clear();
        PawnCursor.Clear();
        DesignationHash.Clear();
        GridHash.Clear();
        ZoneHash.Clear();
    }

    public static void NotifySpawned(Thing thing)
    {
        if (Active)
            Spawned.Add(thing);
    }

    public static void NotifyDespawned(Thing thing)
    {
        if (!Active || thing.Map == null || thing.thingIDNumber < 0)
            return;
        Spawned.Remove(thing);
        if (!Despawned.TryGetValue(thing.Map.uniqueID, out var list))
            Despawned[thing.Map.uniqueID] = list = new List<int>();
        list.Add(thing.thingIDNumber);
    }

    public static void Update(ClientSession session)
    {
        Guests.RemoveWhere(id => !session.Players.Any(p => p.Id == id));
        if (!Active || Guests.Count == 0 || !ScribeMemory.Idle)
        {
            Spawned.Clear();
            Despawned.Clear();
            return;
        }

        var now = Time.realtimeSinceStartup;
        if (now - _lastBatch < BatchInterval)
            return;
        _lastBatch = now;
        var slow = now - _lastSlow >= SlowInterval;
        if (slow)
            _lastSlow = now;

        var batch = new CoopBatch { Tick = Find.TickManager.TicksGame };
        foreach (var map in Find.Maps)
            batch.Maps.Add(BuildDelta(map, slow));
        Spawned.Clear();
        Despawned.Clear();
        session.SendCoop(CoopChannel.State, Compress(batch.Encode()));
    }

    private static MapDelta BuildDelta(Map map, bool slow)
    {
        var delta = new MapDelta { MapId = map.uniqueID };

        foreach (var pawn in map.mapPawns.AllPawnsSpawned)
            delta.Positions.Add(new PawnPosition(pawn.thingIDNumber, pawn.Position.x, pawn.Position.z, (byte)pawn.Rotation.AsInt));

        if (Despawned.TryGetValue(map.uniqueID, out var gone))
            delta.Despawned.AddRange(gone);

        foreach (var thing in Spawned.Where(t => t.Spawned && t.Map == map))
            AddThing(delta, thing, force: true);

        // Rolling re-send: a few things per batch; only those whose saved state changed since last time.
        var things = map.listerThings.AllThings;
        if (things.Count > 0)
        {
            var cursor = ThingCursor.TryGetValue(map.uniqueID, out var c) ? c : 0;
            for (var i = 0; i < ThingsPerBatch && i < things.Count; i++)
            {
                cursor = (cursor + 1) % things.Count;
                var thing = things[cursor];
                if (thing is not Pawn && !Spawned.Contains(thing))
                    AddThing(delta, thing, force: false);
            }
            ThingCursor[map.uniqueID] = cursor;
        }

        // One pawn per batch: health, needs, gear, everything on the inspect tabs.
        var pawns = map.mapPawns.AllPawnsSpawned;
        if (pawns.Count > 0)
        {
            var cursor = PawnCursor.TryGetValue(map.uniqueID, out var c) ? (c + 1) % pawns.Count : 0;
            PawnCursor[map.uniqueID] = cursor;
            if (!Spawned.Contains(pawns[cursor]))
                AddThing(delta, pawns[cursor], force: false);
        }

        if (slow)
        {
            var designations = map.designationManager.AllDesignations
                .Select(d => new DesignationEntry(d.def.defName, d.target.HasThing ? d.target.Thing.thingIDNumber : -1, d.target.Cell.x, d.target.Cell.z))
                .ToList();
            var hash = Hash(designations.Select(d => $"{d.DefName}:{d.ThingId}:{d.X}:{d.Z}"));
            if (!DesignationHash.TryGetValue(map.uniqueID, out var old) || old != hash)
            {
                DesignationHash[map.uniqueID] = hash;
                delta.Designations = designations;
            }

            var grids = ScribeMemory.Save(() =>
            {
                map.terrainGrid.ExposeData();
                map.roofGrid.ExposeData();
                map.fogGrid.ExposeData();
            });
            var gridHash = grids.GetHashCode() ^ grids.Length;
            if (!GridHash.TryGetValue(map.uniqueID, out var oldGrid) || oldGrid != gridHash)
            {
                GridHash[map.uniqueID] = gridHash;
                delta.Grids = grids;
            }

            var zones = ScribeMemory.Save(() => map.zoneManager.ExposeData());
            var zoneHash = zones.GetHashCode() ^ zones.Length;
            if (!ZoneHash.TryGetValue(map.uniqueID, out var oldZones) || oldZones != zoneHash)
            {
                ZoneHash[map.uniqueID] = zoneHash;
                delta.Zones = zones;
            }
        }

        return delta;
    }

    private static void AddThing(MapDelta delta, Thing thing, bool force)
    {
        // Things without an id (rare, purely visual ones) can't be matched up on the guest's side.
        if (thing.thingIDNumber < 0)
            return;

        string xml;
        try
        {
            xml = ScribeMemory.SaveThing(thing);
        }
        catch (Exception e)
        {
            Log.WarningOnce($"[RimMult] Co-op: could not save {thing}: {e.Message}", thing.thingIDNumber ^ 0x434F4F50);
            return;
        }

        var hash = xml.GetHashCode() ^ xml.Length;
        if (!force && SentHash.TryGetValue(thing.thingIDNumber, out var old) && old == hash)
            return;
        SentHash[thing.thingIDNumber] = hash;
        delta.Things.Add(xml);
    }

    private static int Hash(IEnumerable<string> parts)
    {
        unchecked
        {
            var hash = 17;
            foreach (var part in parts)
                hash = hash * 31 + part.GetHashCode();
            return hash;
        }
    }

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
                // Everything after this save reaches the guest as batches; grids and designations go out in full again.
                DesignationHash.Clear();
                GridHash.Clear();
                ZoneHash.Clear();
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
