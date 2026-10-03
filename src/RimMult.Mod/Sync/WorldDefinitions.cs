using System.Collections.Generic;
using System.Linq;
using RimMult.Shared.World;
using RimWorld;
using RimWorld.Planet;
using Verse;

namespace RimMult.Sync;

internal static class WorldDefinitions
{
    public static WorldDefinition FromCurrentWorld(string worldId)
    {
        var info = Find.World.info;
        return new WorldDefinition
        {
            WorldId = worldId,
            SeedString = info.seedString,
            PlanetCoverage = info.planetCoverage,
            Rainfall = (byte)info.overallRainfall,
            Temperature = (byte)info.overallTemperature,
            Population = (byte)info.overallPopulation,
            LandmarkDensity = (byte)info.landmarkDensity,
            Pollution = info.pollution,
            Factions = info.factions?.Select(f => f.defName).ToList() ?? new List<string>(),
        };
    }

    /// <summary>Faction defs to generate with; null means "the game's defaults" (also for old saves without a list).</summary>
    public static List<FactionDef>? ResolveFactions(WorldDefinition definition)
    {
        if (definition.Factions.Count == 0)
            return null;

        var factions = new List<FactionDef>();
        foreach (var name in definition.Factions)
        {
            var def = DefDatabase<FactionDef>.GetNamedSilentFail(name);
            if (def != null)
                factions.Add(def);
            else
                Log.Warning($"[RimMult] World uses unknown faction '{name}'; skipping it.");
        }
        return factions;
    }

    public static OverallRainfall Rainfall(WorldDefinition d) => (OverallRainfall)d.Rainfall;
    public static OverallTemperature Temperature(WorldDefinition d) => (OverallTemperature)d.Temperature;
    public static OverallPopulation Population(WorldDefinition d) => (OverallPopulation)d.Population;
    public static LandmarkDensity Landmarks(WorldDefinition d) => (LandmarkDensity)d.LandmarkDensity;
}
