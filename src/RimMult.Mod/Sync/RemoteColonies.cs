using System;
using System.Collections.Generic;
using System.Linq;
using RimMult.ClientCore;
using RimMult.Shared.World;
using RimMult.Steam;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace RimMult.Sync;

/// <summary>Another player's colony, drawn on this player's globe. Not saved: rebuilt from the server's list.</summary>
public sealed class RemoteColony : WorldObject
{
    public ulong OwnerSteamId;
    public string OwnerName = "";
    public string ColonyName = "";

    public override string Label => ColonyName.NullOrEmpty() ? OwnerName : $"{ColonyName} ({OwnerName})";

    public override Color ExpandingIconColor => PlayerColor(OwnerSteamId);

    public override string GetInspectString() => "RimMult.RemoteColonyInspect".Translate(OwnerName);

    /// <summary>A stable, distinct color per player (golden-ratio hue spacing).</summary>
    public static Color PlayerColor(ulong steamId) => Color.HSVToRGB((float)(steamId * 0.6180339887 % 1.0), 0.55f, 1f);
}

internal static class RemoteColonies
{
    /// <summary>Other players' colonies can't be founded on or right next to.</summary>
    private const float MinDistanceTiles = 4f;

    private static RimWorld.Planet.World? _world;
    private static IReadOnlyList<ColonyInfo>? _applied;
    private static readonly List<RemoteColony> Objects = new();

    public static bool Any => Objects.Count > 0;

    public static void Reconcile(ClientSession session, bool show)
    {
        var world = Current.Game?.World;
        var wanted = show && world != null ? session.Colonies : null;
        if (world == _world && ReferenceEquals(wanted, _applied))
            return;

        Clear();
        _world = world;
        _applied = wanted;
        if (wanted == null || world == null)
            return;

        var me = SteamIntegration.Available ? SteamIntegration.MySteamId : 0;
        foreach (var colony in wanted)
        {
            if (colony.OwnerSteamId == me || !PlanetTile.TryParse(colony.Tile, out var tile) || !tile.Valid)
                continue;
            try
            {
                var obj = (RemoteColony)WorldObjectMaker.MakeWorldObject(RimMultDefOf.RimMult_RemoteColony);
                obj.Tile = tile;
                obj.OwnerSteamId = colony.OwnerSteamId;
                obj.OwnerName = colony.OwnerName;
                obj.ColonyName = colony.Name;
                world.worldObjects.Add(obj);
                Objects.Add(obj);
            }
            catch (Exception e)
            {
                Log.Warning($"[RimMult] Could not show {colony.OwnerName}'s colony at {colony.Tile}: {e.Message}");
            }
        }
    }

    public static void Clear()
    {
        foreach (var obj in Objects)
        {
            if (!obj.Destroyed && _world != null && _world == Current.Game?.World)
                _world.worldObjects.Remove(obj);
        }
        Objects.Clear();
        _world = null;
        _applied = null;
    }

    /// <summary>Whether <paramref name="tile"/> is too close to another player's colony to settle.</summary>
    public static bool IsNearOtherPlayer(PlanetTile tile)
    {
        var session = Multiplayer.Session;
        if (session == null || session.World == null || Find.World == null)
            return false;

        var me = SteamIntegration.Available ? SteamIntegration.MySteamId : 0;
        foreach (var colony in session.Colonies)
        {
            if (colony.OwnerSteamId == me || !PlanetTile.TryParse(colony.Tile, out var other) || !other.Valid)
                continue;
            if (other.Layer != tile.Layer)
                continue;
            if (other == tile || Find.WorldGrid.ApproxDistanceInTiles(tile, other) < MinDistanceTiles)
                return true;
        }
        return false;
    }
}
