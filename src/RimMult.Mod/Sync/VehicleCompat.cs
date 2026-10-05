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
/// of its own kind and never goes to another player's game (its path grids, handlers and upgrades belong to this one).
/// Its caravan lists its passengers among the caravan's pawns, though they sit inside the vehicles: whoever leaves
/// for another game (raiders) is taken out of their vehicle first, and the vehicles wait where they are.
/// </summary>
internal static class VehicleCompat
{
    private static readonly Type? VehicleType = AccessTools.TypeByName("Vehicles.VehiclePawn");
    private static readonly MethodInfo? RemovePawnMethod = VehicleType == null ? null : AccessTools.Method(VehicleType, "RemovePawn", new[] { typeof(Pawn) });
    private static readonly PropertyInfo? AboardProperty = VehicleType == null ? null : AccessTools.Property(VehicleType, "AllPawnsAboard");

    public static bool IsVehicle(Pawn pawn) => VehicleType != null && VehicleType.IsInstanceOfType(pawn);

    public static bool HasVehicles(Caravan caravan) => VehicleType != null && caravan.PawnsListForReading.Any(IsVehicle);

    /// <summary>The caravan's people and animals: everyone but the vehicles themselves (passengers included).</summary>
    public static List<Pawn> Crew(Caravan caravan) => caravan.PawnsListForReading.Where(p => !IsVehicle(p)).ToList();

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
            if (AboardProperty?.GetValue(vehicle) is List<Pawn> aboard && aboard.Contains(pawn))
            {
                RemovePawnMethod?.Invoke(vehicle, new object[] { pawn });
                return;
            }
        }
        caravan.RemovePawn(pawn);
    }

    /// <summary>A caravan of this player's with vehicles waiting on <paramref name="tile"/> (for its crew coming back).</summary>
    public static Caravan? WaitingAt(PlanetTile tile) =>
        VehicleType == null ? null : Find.WorldObjects.Caravans.FirstOrDefault(c => c.Tile == tile && c.IsPlayerControlled && HasVehicles(c));
}
