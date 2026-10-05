using System;
using System.Collections.Generic;
using System.Linq;
using RimMult.ClientCore;
using RimMult.Shared.Packets;
using RimMult.Shared.World;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace RimMult.Sync;

/// <summary>The world's prices in this game: the server's numbers turned into factors for this game's items.</summary>
internal static class WorldPrices
{
    /// <summary>How much dearer (or cheaper) <paramref name="def"/> is on the world market now: its own factor times running news.</summary>
    public static float FactorOf(ThingDef def)
    {
        var session = Multiplayer.Session;
        if (session == null)
            return 1f;
        var prices = session.Market.Prices;
        return prices.ItemFactor(def.defName) * prices.NewsFactor(CategoriesOf(def), DiplomacyUi.Now(session));
    }

    public static float FactorOf(string defName) =>
        DefDatabase<ThingDef>.GetNamedSilentFail(defName) is { } def ? FactorOf(def) : 1f;

    /// <summary>An item's categories with all their parents ("Medicine" for herbal medicine, "Weapons" for a rifle…).</summary>
    public static IEnumerable<string> CategoriesOf(ThingDef def)
    {
        if (def.thingCategories == null)
            yield break;
        foreach (var category in def.thingCategories)
        {
            for (var c = category; c != null; c = c.parent)
                yield return c.defName;
        }
    }
}

/// <summary>
/// The broker's side of the NPC factions on the world market (this game is the broker when the server says so: the
/// host, or the player in the world longest). Every half a game day it may put up a lot from a faction's usual stock,
/// post an order, deliver an order no player has taken for a while (at a higher price), or buy a lot priced well below
/// its worth. Each colony keeps its own relations: the buyer's or seller's game changes its goodwill when the parcel
/// arrives.
/// </summary>
internal static class MarketBroker
{
    private const int RoundTicks = GenDate.TicksPerDay / 2;
    private const float CheckSeconds = 5f;

    /// <summary>Categories NPC orders ask for: everyday goods any colony can make or find.</summary>
    private static readonly HashSet<string> OrderCategories = new()
    {
        "Medicine", "Manufactured", "ResourcesRaw", "Textiles", "FoodMeals", "FoodRaw", "Drugs", "StoneBlocks",
    };

    private static long _nextRound;
    private static float _lastCheck;

    public static void Reset() => _nextRound = 0;

    /// <summary>Called every frame while in the world.</summary>
    public static void Update(ClientSession session)
    {
        if (!session.IsMarketBroker || Current.ProgramState != ProgramState.Playing || Find.TickManager == null)
            return;
        var now = Time.realtimeSinceStartup;
        if (now - _lastCheck < CheckSeconds)
            return;
        _lastCheck = now;
        var tick = Find.TickManager.TicksAbs;
        if (_nextRound == 0)
            _nextRound = tick + RoundTicks / 4; // the first round soon after becoming the broker
        if (tick < _nextRound)
            return;
        _nextRound = tick + RoundTicks;

        try
        {
            Round(session, tick);
        }
        catch (Exception e)
        {
            Log.Warning($"[RimMult] Market broker: {e}");
        }
    }

    private static void Round(ClientSession session, long tick)
    {
        var factions = Traders().ToList();
        if (factions.Count == 0)
            return;
        var market = session.Market;
        if (session.MarketLots.Count(l => l.IsNpc) < market.NpcMaxLots)
            PostLot(session, factions.RandomElement());
        if (session.MarketOrders.Count(o => o.IsNpc) < market.NpcMaxOrders && Rand.Chance(0.5f))
            PostOrder(session, factions.RandomElement());
        DeliverStaleOrder(session, factions, tick);
        BuyCheapLot(session, factions, tick);
    }

    /// <summary>Factions that trade: visible, alive, not everyone's enemy, with traders of their own.</summary>
    private static IEnumerable<Faction> Traders() =>
        Find.FactionManager.AllFactionsListForReading.Where(f =>
            !f.IsPlayer && !f.Hidden && !f.defeated && !f.deactivated && !f.temporary && !f.def.permanentEnemy
            && (f.def.baseTraderKinds.Count > 0 || f.def.caravanTraderKinds.Count > 0));

    private static MarketAction For(Faction faction, MarketActionKind kind)
    {
        var (def, index) = NpcLayoutSync.KeyOf(faction);
        return new MarketAction { Kind = kind, NpcFaction = def, NpcFactionIndex = index, NpcName = faction.Name ?? def };
    }

    /// <summary>One stack from the faction's usual stock, a bit dearer than it's worth.</summary>
    private static void PostLot(ClientSession session, Faction faction)
    {
        var trader = faction.def.baseTraderKinds.Concat(faction.def.caravanTraderKinds).RandomElement();
        var tile = Find.WorldObjects.Settlements.FirstOrDefault(s => s.Faction == faction)?.Tile ?? Find.AnyPlayerHomeMap?.Tile ?? PlanetTile.Invalid;
        var stock = new List<Thing>();
        foreach (var generator in trader.stockGenerators)
        {
            try
            {
                stock.AddRange(generator.GenerateThings(tile, faction));
            }
            catch (Exception)
            {
                // A mod's generator that needs a real trader: skip it.
            }
        }
        var candidates = stock.Where(t => t is not Pawn && t.def.category == ThingCategory.Item && t.def != ThingDefOf.Silver
                                          && ThingPackage.CanSend(t) && t.MarketValue * t.stackCount >= 150f).ToList();
        if (candidates.Count == 0)
            return;
        var thing = candidates.RandomElement();
        var value = thing.MarketValue * thing.stackCount;
        var price = Mathf.Max(1, Mathf.RoundToInt(value * WorldPrices.FactorOf(thing.def) * Rand.Range(1.1f, 1.4f)));
        var things = new List<Thing> { thing };
        var summary = ThingPackage.Summarize(things);
        var action = For(faction, MarketActionKind.NpcPostLot);
        action.Price = price;
        action.Value = value;
        action.DefName = thing.def.defName;
        action.Summary = summary;
        action.Days = Rand.RangeInclusive(6, 12);
        action.Payload = ThingPackage.Pack(things);
        session.SendMarket(action);
    }

    /// <summary>An everyday item the faction needs, paid better than it's worth.</summary>
    private static void PostOrder(ClientSession session, Faction faction)
    {
        var defs = DefDatabase<ThingDef>.AllDefsListForReading.Where(d =>
            d.category == ThingCategory.Item && !d.IsCorpse && !d.MadeFromStuff && !d.destroyOnDrop && d.tradeability != Tradeability.None
            && d.BaseMarketValue >= 1f && d.BaseMarketValue <= 200f && d.techLevel <= faction.def.techLevel
            && WorldPrices.CategoriesOf(d).Any(OrderCategories.Contains)).ToList();
        if (defs.Count == 0)
            return;
        var def = defs.RandomElement();
        var unit = def.BaseMarketValue * WorldPrices.FactorOf(def);
        var count = Mathf.Clamp(Mathf.CeilToInt(Rand.Range(300f, 1500f) / unit), 1, def.stackLimit * 5);
        var reward = Mathf.Max(1, Mathf.RoundToInt(count * unit * Rand.Range(1.2f, 1.6f)));
        var silver = Silver(reward);
        var action = For(faction, MarketActionKind.NpcPostOrder);
        action.DefName = def.defName;
        action.Label = def.label;
        action.Count = count;
        action.Price = reward;
        action.Days = Rand.RangeInclusive(8, 15);
        action.Summary = ThingPackage.Summarize(silver);
        action.Payload = ThingPackage.Pack(silver);
        session.SendMarket(action);
    }

    /// <summary>A player's order nobody took for two days: a faction delivers it if the reward is worth it.</summary>
    private static void DeliverStaleOrder(ClientSession session, List<Faction> factions, long tick)
    {
        foreach (var order in session.MarketOrders.Where(o => !o.IsNpc && tick - o.PostedTick >= 2L * GenDate.TicksPerDay).OrderBy(_ => Rand.Value))
        {
            var def = DefDatabase<ThingDef>.GetNamedSilentFail(order.DefName);
            if (def == null || def.category != ThingCategory.Item || def.tradeability == Tradeability.None)
                continue;
            if (order.Reward < order.Count * def.BaseMarketValue * WorldPrices.FactorOf(def) * 1.1f)
                continue;
            var faction = factions.Where(f => f.def.techLevel >= def.techLevel).RandomElementWithFallback();
            if (faction == null)
                continue;
            var goods = Make(def, order.Count);
            var action = For(faction, MarketActionKind.NpcFulfillOrder);
            action.Id = order.Id;
            action.Summary = ThingPackage.Summarize(goods);
            action.Payload = ThingPackage.Pack(goods);
            session.SendMarket(action);
            return;
        }
    }

    /// <summary>A player's lot priced well under its worth, up for a day already: a faction buys it.</summary>
    private static void BuyCheapLot(ClientSession session, List<Faction> factions, long tick)
    {
        var lot = session.MarketLots
            .Where(l => !l.IsNpc && tick - l.PostedTick >= GenDate.TicksPerDay && l.Price <= l.Value * WorldPrices.FactorOf(l.DefName) * 0.85f)
            .OrderBy(_ => Rand.Value)
            .FirstOrDefault();
        if (lot == null)
            return;
        var silver = Silver(lot.Price);
        var action = For(factions.RandomElement(), MarketActionKind.NpcBuyLot);
        action.Id = lot.Id;
        action.Price = lot.Price;
        action.Summary = ThingPackage.Summarize(silver);
        action.Payload = ThingPackage.Pack(silver);
        session.SendMarket(action);
    }

    private static List<Thing> Silver(int amount) => Make(ThingDefOf.Silver, amount);

    /// <summary>New things of <paramref name="def"/>, <paramref name="count"/> in all, in stacks (never spawned here).</summary>
    private static List<Thing> Make(ThingDef def, int count)
    {
        var things = new List<Thing>();
        while (count > 0)
        {
            var thing = ThingMaker.MakeThing(def, def.MadeFromStuff ? GenStuff.DefaultStuffFor(def) : null);
            thing.stackCount = Math.Min(count, def.stackLimit);
            count -= thing.stackCount;
            things.Add(thing);
        }
        return things;
    }
}
