using System;
using System.Collections.Generic;
using System.Linq;
using RimMult.ClientCore;
using RimMult.Shared.World;
using RimMult.Steam;
using RimMult.UI;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace RimMult.Sync;

/// <summary>Another player's colony, drawn on this player's globe. Not saved: rebuilt from the server's list.</summary>
[StaticConstructorOnStartup]
public sealed class RemoteColony : WorldObject
{
    public ulong OwnerSteamId;
    public string OwnerName = "";
    public string ColonyName = "";
    public byte ColorIndex;

    public override string Label => ColonyName.NullOrEmpty() ? OwnerName : $"{ColonyName} ({OwnerName})";

    /// <summary>The owner's color, both zoomed out (icon) and zoomed in (settlement graphic).</summary>
    public override Color ExpandingIconColor => PlayerPalette.Get(ColorIndex);

    public override Material Material
    {
        get
        {
            try
            {
                return PlayerPalette.WorldMaterial(def.texture, ColorIndex);
            }
            catch (Exception)
            {
                return base.Material;
            }
        }
    }

    public override string GetInspectString()
    {
        var text = "RimMult.RemoteColonyInspect".Translate(OwnerName).ToString();
        if (Multiplayer.Session is { } session)
        {
            text += "\n" + "RimMult.RelationInspect".Translate(DiplomacyUi.Label(session.RelationWith(OwnerSteamId)));
            foreach (var treaty in session.TreatiesWith(OwnerSteamId))
                text += "\n" + DiplomacyUi.TreatyLine(treaty, session, OwnerName);
            if (session.Stats.FirstOrDefault(s => s.Owner == OwnerSteamId) is { } stats)
                text += "\n" + Reputation.Inspect(stats);
        }
        return text;
    }

    /// <summary>Transport pods can be launched here: the items arrive at this player's colony.</summary>
    public override IEnumerable<FloatMenuOption> GetTransportersFloatMenuOptions(
        IEnumerable<IThingHolder> pods, Action<PlanetTile, TransportersArrivalAction> launchAction)
    {
        foreach (var option in base.GetTransportersFloatMenuOptions(pods, launchAction))
            yield return option;
        foreach (var option in TransportersArrivalActionUtility.GetFloatMenuOptions(
                     () => TransportersArrivalAction_SendToPlayer.CanSend(pods),
                     () => new TransportersArrivalAction_SendToPlayer(this),
                     "RimMult.PodsSendTo".Translate(OwnerName),
                     launchAction,
                     Tile))
            yield return option;
        if (Multiplayer.Session?.RelationWith(OwnerSteamId) == Shared.World.PlayerRelation.Allied)
        {
            foreach (var option in TransportersArrivalActionUtility.GetFloatMenuOptions(
                         () => PlayerVisits.CanHelp(this, pods.SelectMany(p => p.GetDirectlyHeldThings()).OfType<Pawn>().ToList()),
                         () => new TransportersArrivalAction_HelpPlayer(this),
                         "RimMult.HelpPods".Translate(OwnerName),
                         launchAction,
                         Tile))
                yield return option;
        }
        if (Multiplayer.Session is { AllowPvp: true } session && session.RelationWith(OwnerSteamId) == Shared.World.PlayerRelation.Hostile)
        {
            foreach (var live in new[] { true, false })
            {
                foreach (var option in TransportersArrivalActionUtility.GetFloatMenuOptions(
                             () => TransportersArrivalAction_RaidPlayer.CanAttack(pods, this),
                             () => new TransportersArrivalAction_RaidPlayer(this, live),
                             (live ? "RimMult.RaidPodsLive" : "RimMult.RaidPods").Translate(OwnerName),
                             launchAction,
                             Tile))
                    yield return option;
            }
        }
    }

    /// <summary>"Offer a trade" while the owner is playing in the world too.</summary>
    public override IEnumerable<Gizmo> GetGizmos()
    {
        foreach (var gizmo in base.GetGizmos())
            yield return gizmo;

        var session = Multiplayer.Session;
        var owner = session?.Players.FirstOrDefault(p => Multiplayer.OwnerKey(p) == OwnerSteamId);
        // At war a trade is a ransom: silver and prisoners only.
        var atWar = session?.RelationWith(OwnerSteamId) == Shared.World.PlayerRelation.Hostile;
        var command = new Command_Action
        {
            defaultLabel = (atWar ? "RimMult.TradeRansom" : "RimMult.TradeOffer").Translate(),
            defaultDesc = (atWar ? "RimMult.TradeRansomDesc" : "RimMult.TradeOfferDesc").Translate(OwnerName),
            icon = TradeIcon,
            action = () => Multiplayer.StartTrade(owner!.Id),
        };
        if (owner == null || !owner.InWorld)
            command.Disable("RimMult.TradeOwnerAway".Translate(OwnerName));
        else if (!WorldSync.InWorld)
            command.Disable("RimMult.TradeNotInWorld".Translate());
        yield return command;

        yield return DiplomacyUi.Gizmo(OwnerSteamId, OwnerName);

        if (session?.RelationWith(OwnerSteamId) == Shared.World.PlayerRelation.Allied)
        {
            var watch = new Command_Action
            {
                defaultLabel = "RimMult.Watch".Translate(),
                defaultDesc = "RimMult.WatchDesc".Translate(OwnerName),
                icon = WatchIcon,
                action = () => Find.WindowStack.Add(Dialog_MessageBox.CreateConfirmation(
                    "RimMult.WatchConfirm".Translate(OwnerName), () => PlayerVisit.Start(OwnerSteamId, OwnerName, watch: true))),
            };
            if (owner == null || !owner.InWorld)
                watch.Disable("RimMult.TradeOwnerAway".Translate(OwnerName));
            else if (!WorldSync.InWorld)
                watch.Disable("RimMult.TradeNotInWorld".Translate());
            yield return watch;
        }
    }

    private static Texture2D? _watchIcon;

    private static Texture2D WatchIcon => _watchIcon ??=
        ContentFinder<Texture2D>.Get("UI/Commands/ViewQuest", reportFailure: false)
        ?? ContentFinder<Texture2D>.Get("UI/Commands/FormCaravan", reportFailure: false)
        ?? BaseContent.BadTex;

    private static Texture2D? _tradeIcon;

    private static Texture2D TradeIcon => _tradeIcon ??=
        ContentFinder<Texture2D>.Get("UI/Commands/Trade", reportFailure: false)
        ?? ContentFinder<Texture2D>.Get("UI/Commands/FormCaravan", reportFailure: false)
        ?? BaseContent.BadTex;

    /// <summary>A caravan can travel here to trade, hand over its cargo, or join the colony.</summary>
    public override IEnumerable<FloatMenuOption> GetFloatMenuOptions(Caravan caravan)
    {
        foreach (var option in base.GetFloatMenuOptions(caravan))
            yield return option;
        if (Multiplayer.Session is { AllowPvp: true } session && session.RelationWith(OwnerSteamId) == Shared.World.PlayerRelation.Hostile)
        {
            // A caravan with vehicles: attack on them (they drive in, crew inside), or with the crew alone.
            var vehicles = VehicleCompat.HasVehicles(caravan);
            foreach (var withVehicles in vehicles ? new[] { true, false } : new[] { false })
            {
                foreach (var live in new[] { true, false })
                {
                    foreach (var option in CaravanArrivalActionUtility.GetFloatMenuOptions(
                                 () => CaravanArrivalAction_RaidPlayer.CanAttack(caravan, this, withVehicles),
                                 () => new CaravanArrivalAction_RaidPlayer(this, live, withVehicles),
                                 CaravanArrivalAction_RaidPlayer.LabelFor(OwnerName, live, withVehicles, crewOnly: vehicles && !withVehicles),
                                 caravan,
                                 Tile,
                                 this))
                        yield return option;
                }
            }
            // No trading, gifts or moving in with an enemy.
            yield break;
        }
        if (Multiplayer.Session?.RelationWith(OwnerSteamId) == Shared.World.PlayerRelation.Allied)
        {
            foreach (var option in CaravanArrivalActionUtility.GetFloatMenuOptions(
                         () => PlayerVisits.CanHelp(this, caravan.PawnsListForReading),
                         () => new CaravanArrivalAction_HelpPlayer(this),
                         "RimMult.HelpGo".Translate(OwnerName),
                         caravan,
                         Tile,
                         this))
                yield return option;
        }
        foreach (var option in CaravanArrivalActionUtility.GetFloatMenuOptions(
                     () => true,
                     () => new CaravanArrivalAction_TradeWithPlayer(this),
                     "RimMult.CaravanTrade".Translate(OwnerName),
                     caravan,
                     Tile,
                     this))
            yield return option;
        foreach (var option in CaravanArrivalActionUtility.GetFloatMenuOptions(
                     () => CaravanArrivalAction_GiveToPlayer.CanGive(caravan),
                     () => new CaravanArrivalAction_GiveToPlayer(this),
                     "RimMult.CaravanGive".Translate(OwnerName),
                     caravan,
                     Tile,
                     this))
            yield return option;
        foreach (var option in CaravanArrivalActionUtility.GetFloatMenuOptions(
                     () => CaravanArrivalAction_JoinPlayer.CanJoin(caravan),
                     () => new CaravanArrivalAction_JoinPlayer(this),
                     "RimMult.CaravanJoin".Translate(OwnerName),
                     caravan,
                     Tile,
                     this))
            yield return option;
    }

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
                obj.ColorIndex = colony.ColorIndex;
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
