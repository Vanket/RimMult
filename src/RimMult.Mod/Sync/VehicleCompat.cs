using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using RimWorld.Planet;
using Verse;

namespace RimMult.Sync;

/// <summary>
/// Vehicle Framework (vehicles from Vanilla Vehicles Expanded and others), without depending on it. A vehicle is a pawn
/// of its own kind with its passengers inside (in its role handlers, saved with it). Its caravan lists the passengers
/// among the caravan's pawns too (VF patches <c>Caravan.PawnsListForReading</c>), though they sit in the vehicles.
/// <para>
/// Vehicles only go to another player's game in a raid "on vehicles": the vehicles drive in with their crew inside and
/// attack under VF's own AI, as NPC raiders' vehicles do. Gifts, moving over and helping take no vehicles; a raid "crew
/// only" leaves them waiting by the colony.
/// </para>
/// </summary>
internal static class VehicleCompat
{
    private static readonly Type? VehicleType = AccessTools.TypeByName("Vehicles.VehiclePawn");
    private static readonly MethodInfo? RemovePawnMethod = VehicleType == null ? null : AccessTools.Method(VehicleType, "RemovePawn", new[] { typeof(Pawn) });
    private static readonly MethodInfo? DestroyWithCrewMethod = VehicleType == null ? null : AccessTools.Method(VehicleType, "DestroyVehicleAndPawns");
    private static readonly PropertyInfo? AboardProperty = VehicleType == null ? null : AccessTools.Property(VehicleType, "AllPawnsAboard");
    private static readonly PropertyInfo? VehicleDefProperty = VehicleType == null ? null : AccessTools.Property(VehicleType, "VehicleDef");

    private static readonly MethodInfo? ClosewalkCellMethod = AccessTools.TypeByName("Vehicles.CellFinderExtended") is { } finder
        ? AccessTools.GetDeclaredMethods(finder).FirstOrDefault(m => m.Name == "RandomClosewalkCellNear" && m.GetParameters().Length == 5)
        : null;

    public static bool IsVehicle(Pawn pawn) => VehicleType != null && VehicleType.IsInstanceOfType(pawn);

    public static bool HasVehicles(Caravan caravan) => VehicleType != null && caravan.PawnsListForReading.Any(IsVehicle);

    /// <summary>The caravan's people and animals: everyone but the vehicles themselves (passengers included).</summary>
    public static List<Pawn> Crew(Caravan caravan) => caravan.PawnsListForReading.Where(p => !IsVehicle(p)).ToList();

    /// <summary>The caravan's own members: vehicles (passengers inside them) and whoever walks; nobody twice.</summary>
    public static List<Pawn> Members(Caravan caravan) => caravan.pawns.InnerListForReading.ToList();

    /// <summary>Who rides in a vehicle (empty for anything else).</summary>
    public static List<Pawn> Passengers(Pawn pawn) =>
        IsVehicle(pawn) && AboardProperty?.GetValue(pawn) is List<Pawn> aboard ? aboard.ToList() : new List<Pawn>();

    /// <summary>The pawns and everyone riding in them.</summary>
    public static IEnumerable<Pawn> WithPassengers(IEnumerable<Pawn> pawns) => pawns.SelectMany(p => Passengers(p).Prepend(p));

    /// <summary>Takes a pawn out of the caravan: out of its vehicle if it rides in one.</summary>
    public static void TakeOut(Caravan caravan, Pawn pawn)
    {
        if (caravan.pawns.Contains(pawn))
        {
            caravan.RemovePawn(pawn);
            return;
        }
        foreach (var vehicle in caravan.PawnsListForReading.Where(IsVehicle).ToList())
        {
            if (Passengers(vehicle).Contains(pawn))
            {
                RemovePawnMethod?.Invoke(vehicle, new object[] { pawn });
                return;
            }
        }
        caravan.RemovePawn(pawn);
    }

    /// <summary>A vehicle that left for another game: gone from this one with everyone inside (nobody gets out).</summary>
    public static void Vanish(Pawn vehicle)
    {
        foreach (var pawn in Passengers(vehicle).Prepend(vehicle))
        {
            if (Find.WorldPawns.Contains(pawn))
                Find.WorldPawns.RemovePawn(pawn);
        }
        if (DestroyWithCrewMethod != null)
            DestroyWithCrewMethod.Invoke(vehicle, new object[] { DestroyMode.Vanish });
        else if (!vehicle.Destroyed)
            vehicle.Destroy(DestroyMode.Vanish);
    }

    /// <summary>A cell near <paramref name="root"/> where the whole vehicle fits (any cell for anything else).</summary>
    public static IntVec3 SpawnCellNear(Pawn pawn, IntVec3 root, Map map, int radius)
    {
        if (IsVehicle(pawn) && ClosewalkCellMethod != null && VehicleDefProperty?.GetValue(pawn) is { } def)
        {
            try
            {
                if (ClosewalkCellMethod.Invoke(null, new[] { root, map, def, radius, null }) is IntVec3 cell && cell.IsValid)
                    return cell;
            }
            catch (Exception e)
            {
                Log.Warning($"[RimMult] No room found for {pawn.LabelShortCap}: {e.InnerException?.Message ?? e.Message}");
            }
        }
        return CellFinder.RandomClosewalkCellNear(root, map, radius);
    }

    /// <summary>A caravan of this player's with vehicles waiting on <paramref name="tile"/> (for its crew coming back).</summary>
    public static Caravan? WaitingAt(PlanetTile tile) =>
        VehicleType == null ? null : Find.WorldObjects.Caravans.FirstOrDefault(c => c.Tile == tile && c.IsPlayerControlled && HasVehicles(c));
}
