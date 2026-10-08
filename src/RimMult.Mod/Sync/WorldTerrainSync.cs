using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace RimMult.Sync;

/// <summary>
/// One planet for everyone. Each game generates the planet from the seed itself, and mods (or a planet generated
/// long ago with other mods) make rivers, roads, hills and biomes come out differently; so the world's creator packs
/// its planet the way the game saves it (every tile's biome, height, hills, climate, swamps, pollution, rivers, roads,
/// features and landmarks) and every other game takes it over. Tiles under this game's own maps keep their ground
/// (the colony there was built on it); they take the rivers and roads around them.
/// </summary>
internal static class WorldTerrainSync
{
    private const int FormatVersion = 1;

    /// <summary>The surface layer's packed tile arrays, as the game saves them (PlanetLayer + SurfaceLayer).</summary>
    private static readonly string[] Arrays =
    {
        "tileBiome", "tileElevation", "tileHilliness", "tileTemperature", "tileRainfall", "tileSwampiness", "tilePollution",
        "tileFeature", "tileRoadOrigins", "tileRoadAdjacency", "tileRoadDef", "tileRiverOrigins", "tileRiverAdjacency",
        "tileRiverDef", "tileRiverDistances", "tileMutatorTiles", "tileMutatorDefs",
    };

    private static readonly MethodInfo TilesToRawData = AccessTools.Method(typeof(SurfaceLayer), "TilesToRawData");
    private static readonly MethodInfo RawDataToTiles = AccessTools.Method(typeof(SurfaceLayer), "RawDataToTiles");
    private static readonly AccessTools.FieldRef<PlanetLayer, List<Tile>> Tiles = AccessTools.FieldRefAccess<PlanetLayer, List<Tile>>("tiles");

    /// <summary>This game's planet, packed for the other games.</summary>
    public static byte[] Capture()
    {
        var surface = Find.WorldGrid.Surface;
        TilesToRawData.Invoke(surface, null);

        using var raw = new MemoryStream();
        using (var writer = new BinaryWriter(raw))
        {
            writer.Write(FormatVersion);
            writer.Write(surface.TilesCount);
            WriteDefs<BiomeDef>(writer);
            WriteDefs<RiverDef>(writer);
            WriteDefs<RoadDef>(writer);
            WriteDefs<TileMutatorDef>(writer);

            writer.Write(Arrays.Length);
            foreach (var name in Arrays)
            {
                var data = AccessTools.Field(typeof(SurfaceLayer), name)?.GetValue(surface) as byte[] ?? Array.Empty<byte>();
                writer.Write(name);
                writer.Write(data.Length);
                writer.Write(data);
            }

            var features = Find.WorldFeatures.features.Where(f => f.layer == surface && f.def != null).ToList();
            writer.Write(features.Count);
            foreach (var feature in features)
            {
                writer.Write(feature.uniqueID);
                writer.Write(feature.def.defName);
                writer.Write(feature.name ?? "");
                writer.Write(feature.drawCenter.x);
                writer.Write(feature.drawCenter.y);
                writer.Write(feature.drawCenter.z);
                writer.Write(feature.drawAngle);
                writer.Write(feature.maxDrawSizeInTiles);
            }

            var landmarks = Find.World.landmarks.landmarks.Where(p => p.Key.Layer == surface && p.Value?.def != null).ToList();
            writer.Write(landmarks.Count);
            foreach (var pair in landmarks)
            {
                writer.Write(pair.Key.tileId);
                writer.Write(pair.Value.def.defName);
                writer.Write(pair.Value.name ?? "");
                writer.Write(pair.Value.isComboLandmark);
            }
        }
        return Compress(raw.ToArray());
    }

    /// <summary>Takes the planet over. False (and nothing changed) if it doesn't fit this game's planet.</summary>
    public static bool Apply(byte[] packed)
    {
        var surface = Find.WorldGrid.Surface;
        using var reader = new BinaryReader(new MemoryStream(Decompress(packed)));
        var version = reader.ReadInt32();
        if (version != FormatVersion)
        {
            Log.Warning($"[RimMult] The world's planet comes in format {version}, this RimMult reads {FormatVersion}: update RimMult.");
            return false;
        }
        var count = reader.ReadInt32();
        if (count != surface.TilesCount)
        {
            Log.Error($"[RimMult] The world's planet has {count} tiles, this game's {surface.TilesCount}: not the same planet size, left as is.");
            return false;
        }

        var biomes = ReadDefs<BiomeDef>(reader);
        var rivers = ReadDefs<RiverDef>(reader);
        var roads = ReadDefs<RoadDef>(reader);
        var mutators = ReadDefs<TileMutatorDef>(reader);
        var arrays = new Dictionary<string, byte[]>();
        for (int i = 0, n = reader.ReadInt32(); i < n; i++)
        {
            var name = reader.ReadString();
            arrays[name] = reader.ReadBytes(reader.ReadInt32());
        }
        var features = new List<WorldFeature>();
        for (int i = 0, n = reader.ReadInt32(); i < n; i++)
        {
            var id = reader.ReadInt32();
            var def = DefDatabase<FeatureDef>.GetNamedSilentFail(reader.ReadString());
            var name = reader.ReadString();
            var center = new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
            var angle = reader.ReadSingle();
            var size = reader.ReadSingle();
            if (def != null)
                features.Add(new WorldFeature { uniqueID = id, def = def, layer = surface, name = name, drawCenter = center, drawAngle = angle, maxDrawSizeInTiles = size });
        }
        var landmarks = new List<(int Tile, LandmarkDef Def, string Name, bool Combo)>();
        for (int i = 0, n = reader.ReadInt32(); i < n; i++)
        {
            var tile = reader.ReadInt32();
            var def = DefDatabase<LandmarkDef>.GetNamedSilentFail(reader.ReadString());
            var name = reader.ReadString();
            var combo = reader.ReadBoolean();
            if (def != null && tile >= 0 && tile < count)
                landmarks.Add((tile, def, name, combo));
        }

        // Def ids are short hashes: the same with the same mods, but translate by name in case they aren't.
        Remap(arrays, "tileBiome", biomes);
        Remap(arrays, "tileRiverDef", rivers);
        Remap(arrays, "tileRoadDef", roads);
        Remap(arrays, "tileMutatorDefs", mutators);

        // The ground under this game's maps stays: the colony (or the fight, the visit) there was built on it.
        var kept = new Dictionary<int, Tile>();
        foreach (var map in Find.Maps)
        {
            if (map.Tile.Valid && map.Tile.Layer == surface)
                kept[map.Tile.tileId] = surface[map.Tile.tileId];
        }

        foreach (var name in Arrays)
        {
            if (arrays.TryGetValue(name, out var data))
                AccessTools.Field(typeof(SurfaceLayer), name)?.SetValue(surface, data);
        }
        // Fresh tiles, so nothing cached on the old ones (labels, temperatures, secondary biomes) survives.
        var tiles = Tiles(surface);
        tiles.Clear();
        RawDataToTiles.Invoke(surface, null);
        foreach (var pair in kept)
        {
            var fresh = (SurfaceTile)tiles[pair.Key];
            var old = (SurfaceTile)pair.Value;
            old.potentialRoads = fresh.potentialRoads;
            old.potentialRivers = fresh.potentialRivers;
            old.riverDist = fresh.riverDist;
            tiles[pair.Key] = old;
        }

        var worldFeatures = Find.WorldFeatures;
        worldFeatures.features.RemoveAll(f => f.layer == surface);
        worldFeatures.features.AddRange(features);
        if (arrays.TryGetValue("tileFeature", out var featureIds) && featureIds.Length > 0)
            DataSerializeUtility.LoadUshort(featureIds, count, (i, id) => tiles[i].feature = id == ushort.MaxValue ? null : worldFeatures.GetFeatureWithID(id));
        worldFeatures.textsCreated = false;

        var worldLandmarks = Find.World.landmarks.landmarks;
        foreach (var tile in worldLandmarks.Keys.Where(t => t.Layer == surface && !kept.ContainsKey(t.tileId)).ToList())
            worldLandmarks.Remove(tile);
        foreach (var (tile, def, name, combo) in landmarks)
        {
            if (!kept.ContainsKey(tile))
                worldLandmarks[new PlanetTile(tile, surface)] = new Landmark(def) { name = name, isComboLandmark = combo };
        }

        surface.FastTileFinder?.RegenerateCache();
        surface.SetAllLayersDirty();
        Find.WorldPathGrid.RecalculateAllLayersPathCosts();
        Find.WorldReachability.ClearCache();
        Log.Message($"[RimMult] Took over the world's planet: {count} tiles, {features.Count} features, {landmarks.Count} landmarks; kept the ground under {kept.Count} map(s).");
        return true;
    }

    private static void WriteDefs<T>(BinaryWriter writer) where T : Def
    {
        var defs = DefDatabase<T>.AllDefsListForReading;
        writer.Write(defs.Count);
        foreach (var def in defs)
        {
            writer.Write(def.shortHash);
            writer.Write(def.defName);
        }
    }

    /// <summary>The creator's short hash → this game's (0 for a def this game doesn't have: dropped, or the default biome).</summary>
    private static Dictionary<ushort, ushort> ReadDefs<T>(BinaryReader reader) where T : Def
    {
        var map = new Dictionary<ushort, ushort>();
        for (int i = 0, n = reader.ReadInt32(); i < n; i++)
        {
            var hash = reader.ReadUInt16();
            map[hash] = DefDatabase<T>.GetNamedSilentFail(reader.ReadString())?.shortHash ?? 0;
        }
        return map;
    }

    private static void Remap(Dictionary<string, byte[]> arrays, string name, Dictionary<ushort, ushort> map)
    {
        if (!arrays.TryGetValue(name, out var data) || data.Length == 0 || map.All(p => p.Key == p.Value))
            return;
        var values = DataSerializeUtility.DeserializeUshort(data);
        for (var i = 0; i < values.Length; i++)
            values[i] = map.TryGetValue(values[i], out var mine) ? mine : (ushort)0;
        arrays[name] = DataSerializeUtility.SerializeUshort(values);
    }

    private static byte[] Compress(byte[] data)
    {
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionMode.Compress))
            gzip.Write(data, 0, data.Length);
        return output.ToArray();
    }

    private static byte[] Decompress(byte[] data)
    {
        using var input = new GZipStream(new MemoryStream(data), CompressionMode.Decompress);
        using var output = new MemoryStream();
        input.CopyTo(output);
        return output.ToArray();
    }
}
