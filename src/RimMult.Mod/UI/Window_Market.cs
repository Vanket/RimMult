using System;
using System.Collections.Generic;
using System.Linq;
using RimMult.ClientCore;
using RimMult.Shared.Packets;
using RimMult.Shared.World;
using RimMult.Sync;
using RimWorld;
using UnityEngine;
using Verse;

namespace RimMult.UI;

/// <summary>
/// The market's deals, game side: goods and silver leave this colony packed (the server holds them), whatever comes
/// back arrives as a parcel.
/// </summary>
internal static class MarketDeals
{
    /// <summary>Market deals need a colony in the shared world (goods and silver come from its stockpiles).</summary>
    public static bool CanDeal(out string reason)
    {
        reason = "";
        if (Multiplayer.Session is not { State: ClientState.Connected } || !WorldSync.InWorld)
        {
            reason = "RimMult.TradeNotInWorld".Translate();
            return false;
        }
        return true;
    }

    /// <summary>
    /// Pays the market's fee for one more listing (silver burnt here; the server checks the sum). False, with a message,
    /// when there isn't enough silver for it and <paramref name="also"/> on top.
    /// </summary>
    private static bool PayFee(ClientSession session, int price, int also, out int fee)
    {
        fee = session.MarketFeeFor(price);
        if (Goods.SilverAvailable() < fee + also)
        {
            Messages.Message("RimMult.TributeNotEnough".Translate(fee + also, Goods.SilverAvailable()), MessageTypeDefOf.RejectInput, historical: false);
            return false;
        }
        if (fee > 0 && Goods.TakeSilver(fee) is { } silver)
            Goods.Gone(silver);
        return true;
    }

    public static void PostLot(List<TransferableOneWay> chosen, int price)
    {
        var session = Multiplayer.Session;
        if (session == null || price <= 0 || !PayFee(session, price, 0, out var fee))
            return;
        var things = Goods.Take(chosen);
        if (things.Count == 0)
            return;
        var value = things.Sum(t => t.MarketValue * t.stackCount);
        var main = things.OrderByDescending(t => t.MarketValue * t.stackCount).First().def.defName;
        var summary = ThingPackage.Summarize(things);
        if (Goods.Pack(things) is not { } payload)
        {
            Goods.PutBack(things);
            return;
        }
        session.SendMarket(new MarketAction
        {
            Kind = MarketActionKind.PostLot, Price = price, Value = value, DefName = main, Fee = fee, Summary = summary, Payload = payload,
        });
        Goods.Gone(things);
        Messages.Message("RimMult.MarketLotPosted".Translate(summary, price), MessageTypeDefOf.NeutralEvent, historical: false);
    }

    public static void Buy(MarketLot lot)
    {
        var session = Multiplayer.Session;
        if (session == null)
            return;
        if (Goods.TakeSilver(lot.Price) is not { } silver)
        {
            Messages.Message("RimMult.TributeNotEnough".Translate(lot.Price, Goods.SilverAvailable()), MessageTypeDefOf.RejectInput, historical: false);
            return;
        }
        if (Goods.Pack(silver) is not { } payload)
        {
            Goods.PutBack(silver);
            return;
        }
        session.SendMarket(new MarketAction
        {
            Kind = MarketActionKind.BuyLot, Id = lot.Id, Price = lot.Price, Summary = ThingPackage.Summarize(silver), Payload = payload,
        });
        Goods.Gone(silver);
        Messages.Message("RimMult.MarketBuying".Translate(lot.Summary), MessageTypeDefOf.NeutralEvent, historical: false);
    }

    public static void PostOrder(ThingDef def, int count, int reward, int days)
    {
        var session = Multiplayer.Session;
        if (session == null || count <= 0 || reward <= 0 || !PayFee(session, reward, reward, out var fee))
            return;
        if (Goods.TakeSilver(reward) is not { } silver)
        {
            Messages.Message("RimMult.TributeNotEnough".Translate(reward, Goods.SilverAvailable()), MessageTypeDefOf.RejectInput, historical: false);
            return;
        }
        if (Goods.Pack(silver) is not { } payload)
        {
            Goods.PutBack(silver);
            return;
        }
        session.SendMarket(new MarketAction
        {
            Kind = MarketActionKind.PostOrder, DefName = def.defName, Label = def.label, Count = count, Price = reward, Days = days, Fee = fee,
            Summary = ThingPackage.Summarize(silver), Payload = payload,
        });
        Goods.Gone(silver);
        Messages.Message("RimMult.MarketOrderPosted".Translate(def.LabelCap, count, reward), MessageTypeDefOf.NeutralEvent, historical: false);
    }

    public static void Fulfill(MarketOrder order)
    {
        var session = Multiplayer.Session;
        var def = DefDatabase<ThingDef>.GetNamedSilentFail(order.DefName);
        if (session == null || def == null)
            return;
        if (Goods.TakeOf(def, order.Count) is not { } goods)
        {
            Messages.Message("RimMult.MarketNotEnough".Translate(def.LabelCap, order.Count, Goods.CountOf(def)), MessageTypeDefOf.RejectInput, historical: false);
            return;
        }
        var summary = ThingPackage.Summarize(goods);
        if (Goods.Pack(goods) is not { } payload)
        {
            Goods.PutBack(goods);
            return;
        }
        session.SendMarket(new MarketAction { Kind = MarketActionKind.FulfillOrder, Id = order.Id, Summary = summary, Payload = payload });
        Goods.Gone(goods);
        Messages.Message("RimMult.MarketDelivering".Translate(summary, order.BuyerName), MessageTypeDefOf.NeutralEvent, historical: false);
    }

    public static void Cancel(MarketActionKind kind, long id) =>
        Multiplayer.Session?.SendMarket(new MarketAction { Kind = kind, Id = id });

    /// <summary>An NPC faction this colony is at war with: no deals.</summary>
    public static Faction? HostileNpc(string npcFaction, int index) =>
        npcFaction.Length > 0 && NpcLayoutSync.FactionOf(npcFaction, index) is { } faction && faction.HostileTo(Faction.OfPlayer) ? faction : null;
}

/// <summary>The world's market: lots for sale and open orders, with buttons to deal.</summary>
internal sealed class Window_Market : Window
{
    private const float RowHeight = 32f;

    private enum Tab
    {
        Lots,
        Orders,
        Prices,
    }

    private Tab _tab;
    private Vector2 _scroll;

    private Window_Market()
    {
        doCloseX = true;
        draggable = true;
        resizeable = true;
        forcePause = false;
        absorbInputAroundWindow = false;
        closeOnClickedOutside = false;
        preventCameraMotion = false;
        onlyOneOfTypeAllowed = true;
        optionalTitle = "RimMult.MarketTitle".Translate();
    }

    public override Vector2 InitialSize => new(960f, 640f);

    public static void Open()
    {
        if (!Find.WindowStack.TryRemove(typeof(Window_Market)))
            Find.WindowStack.Add(new Window_Market());
    }

    public override void DoWindowContents(Rect inRect)
    {
        var session = Multiplayer.Session;
        if (session == null || session.State != ClientState.Connected)
        {
            Close();
            return;
        }

        Text.Font = GameFont.Small;
        var top = new Rect(inRect.x, inRect.y, inRect.width, 30f);
        if (Widgets.ButtonText(top.LeftPartPixels(160f), "RimMult.MarketTabLots".Translate(session.MarketLots.Count), active: _tab != Tab.Lots))
            _tab = Tab.Lots;
        if (Widgets.ButtonText(new Rect(top.x + 166f, top.y, 160f, top.height), "RimMult.MarketTabOrders".Translate(session.MarketOrders.Count), active: _tab != Tab.Orders))
            _tab = Tab.Orders;
        if (Widgets.ButtonText(new Rect(top.x + 332f, top.y, 160f, top.height), "RimMult.MarketTabPrices".Translate(), active: _tab != Tab.Prices))
            _tab = Tab.Prices;

        var canDeal = MarketDeals.CanDeal(out var why);
        var post = new Rect(top.xMax - 220f, top.y, 220f, top.height);
        if (_tab != Tab.Prices
            && Widgets.ButtonText(post, (_tab == Tab.Orders ? "RimMult.MarketPostOrder" : "RimMult.MarketPostLot").Translate(), active: canDeal) && canDeal)
        {
            if (_tab == Tab.Orders)
                Find.WindowStack.Add(new Dialog_PostOrder());
            else
                Find.WindowStack.Add(new Dialog_PostLot());
        }
        if (!canDeal && _tab != Tab.Prices)
            TooltipHandler.TipRegion(post, why);

        var silver = new Rect(post.x - 230f, top.y, 220f, top.height);
        if (WorldSync.InWorld)
        {
            Text.Anchor = TextAnchor.MiddleRight;
            Widgets.Label(silver, "RimMult.MarketSilver".Translate(Goods.SilverAvailable()));
            Text.Anchor = TextAnchor.UpperLeft;
        }

        var body = new Rect(inRect.x, top.yMax + 8f, inRect.width, inRect.height - top.height - 8f);
        Widgets.DrawMenuSection(body);
        switch (_tab)
        {
            case Tab.Orders:
                DrawOrders(body.ContractedBy(6f), session, canDeal);
                break;
            case Tab.Prices:
                DrawPrices(body.ContractedBy(6f), session);
                break;
            default:
                DrawLots(body.ContractedBy(6f), session, canDeal);
                break;
        }
    }

    /// <summary>The world's prices: running news first, then the items whose price moved most.</summary>
    private void DrawPrices(Rect rect, ClientSession session)
    {
        var prices = session.Market.Prices;
        var now = DiplomacyUi.Now(session);
        var lines = new List<string>();
        foreach (var news in prices.News.Where(n => n.StartTick <= now && now < n.EndTick))
        {
            var days = Mathf.Max(1, Mathf.CeilToInt((news.EndTick - now) / (float)Treaty.TicksPerDay));
            lines.Add(Arrow(news.Factor) + ChronicleUi.MarketNews(news.Category, Mathf.RoundToInt((news.Factor - 1f) * 100f))
                      + " " + "RimMult.MarketNewsDays".Translate(days));
        }
        var moved = prices.Items
            .Select(p => (Def: DefDatabase<ThingDef>.GetNamedSilentFail(p.Key), Factor: p.Value))
            .Where(p => p.Def != null)
            .OrderByDescending(p => Mathf.Abs(p.Factor - 1f))
            .Take(40)
            .ToList();
        if (lines.Count == 0 && moved.Count == 0)
        {
            Empty(rect, "RimMult.MarketPricesNormal");
            return;
        }
        var hint = "RimMult.MarketPricesHint".Translate().ToString();
        var hintHeight = Text.CalcHeight(hint, rect.width);
        GUI.color = Color.gray;
        Widgets.Label(new Rect(rect.x, rect.y, rect.width, hintHeight), hint);
        GUI.color = Color.white;
        var list = new Rect(rect.x, rect.y + hintHeight + 6f, rect.width, rect.height - hintHeight - 6f);
        var view = new Rect(0f, 0f, list.width - 16f, (lines.Count + moved.Count + 1) * 28f);
        Widgets.BeginScrollView(list, ref _scroll, view);
        var y = 0f;
        foreach (var line in lines)
        {
            Widgets.Label(new Rect(0f, y, view.width, 28f), line);
            y += 28f;
        }
        if (lines.Count > 0)
            y += 28f;
        foreach (var (def, factor) in moved)
        {
            Widgets.ThingIcon(new Rect(0f, y + 2f, 24f, 24f), def);
            Widgets.Label(new Rect(30f, y + 2f, view.width - 30f, 28f),
                $"{Arrow(factor)}{def!.LabelCap}: {(factor >= 1f ? "+" : "−")}{Mathf.RoundToInt(Mathf.Abs(factor - 1f) * 100f)} %");
            y += 28f;
        }
        Widgets.EndScrollView();
    }

    private static string Arrow(float factor) => factor >= 1f ? "<color=#E07050>▲</color> " : "<color=#60C060>▼</color> ";

    private void DrawLots(Rect rect, ClientSession session, bool canDeal)
    {
        var lots = session.MarketLots.OrderByDescending(l => l.Id).ToList();
        if (lots.Count == 0)
        {
            Empty(rect, "RimMult.MarketNoLots");
            return;
        }
        var silver = canDeal ? Goods.SilverAvailable() : 0;
        var view = new Rect(0f, 0f, rect.width - 16f, lots.Count * RowHeight);
        Widgets.BeginScrollView(rect, ref _scroll, view);
        for (var i = 0; i < lots.Count; i++)
        {
            var lot = lots[i];
            var row = new Rect(0f, i * RowHeight, view.width, RowHeight);
            if (i % 2 == 1)
                Widgets.DrawLightHighlight(row);
            var button = new Rect(row.xMax - 140f, row.y + 2f, 140f, RowHeight - 4f);
            var price = new Rect(button.x - 180f, row.y, 175f, RowHeight);
            var seller = new Rect(row.x + 4f, row.y, 150f, RowHeight);
            Text.Anchor = TextAnchor.MiddleLeft;
            Widgets.Label(seller, PlayerName(session, lot.Seller, lot.SellerName));
            Widgets.Label(new Rect(seller.xMax + 6f, row.y, price.x - seller.xMax - 12f, RowHeight), lot.Summary.Replace("<", "‹"));
            Widgets.Label(price, "RimMult.MarketPrice".Translate(lot.Price, lot.Value.ToStringMoney()));
            Text.Anchor = TextAnchor.UpperLeft;
            TooltipHandler.TipRegion(row, lot.Summary);

            if (lot.Seller == session.MyOwnerKey)
            {
                if (Widgets.ButtonText(button, "RimMult.MarketTakeBack".Translate(), active: canDeal) && canDeal)
                    MarketDeals.Cancel(MarketActionKind.CancelLot, lot.Id);
            }
            else
            {
                var atWar = lot.IsNpc ? MarketDeals.HostileNpc(lot.NpcFaction, lot.NpcFactionIndex) != null
                    : session.RelationWith(lot.Seller) == PlayerRelation.Hostile;
                var can = canDeal && !atWar && silver >= lot.Price;
                if (Widgets.ButtonText(button, "RimMult.MarketBuy".Translate(), active: can) && can)
                    Find.WindowStack.Add(Dialog_MessageBox.CreateConfirmation(
                        "RimMult.MarketBuyConfirm".Translate(lot.Summary, lot.Price, lot.SellerName), () => MarketDeals.Buy(lot)));
                if (atWar)
                    TooltipHandler.TipRegion(button, "RimMult.MarketAtWar".Translate(lot.SellerName));
                else if (canDeal && silver < lot.Price)
                    TooltipHandler.TipRegion(button, "RimMult.TributeNotEnough".Translate(lot.Price, silver));
            }
        }
        Widgets.EndScrollView();
    }

    private void DrawOrders(Rect rect, ClientSession session, bool canDeal)
    {
        var orders = session.MarketOrders.OrderBy(o => o.DeadlineTick).ToList();
        if (orders.Count == 0)
        {
            Empty(rect, "RimMult.MarketNoOrders");
            return;
        }
        var now = DiplomacyUi.Now(session);
        var view = new Rect(0f, 0f, rect.width - 16f, orders.Count * RowHeight);
        Widgets.BeginScrollView(rect, ref _scroll, view);
        for (var i = 0; i < orders.Count; i++)
        {
            var order = orders[i];
            var def = DefDatabase<ThingDef>.GetNamedSilentFail(order.DefName);
            var row = new Rect(0f, i * RowHeight, view.width, RowHeight);
            if (i % 2 == 1)
                Widgets.DrawLightHighlight(row);
            var button = new Rect(row.xMax - 140f, row.y + 2f, 140f, RowHeight - 4f);
            var reward = new Rect(button.x - 250f, row.y, 245f, RowHeight);
            var buyer = new Rect(row.x + 4f, row.y, 150f, RowHeight);
            var days = Mathf.Max(0, Mathf.CeilToInt((order.DeadlineTick - now) / (float)Treaty.TicksPerDay));
            Text.Anchor = TextAnchor.MiddleLeft;
            Widgets.Label(buyer, PlayerName(session, order.Buyer, order.BuyerName));
            if (def != null)
                Widgets.ThingIcon(new Rect(buyer.xMax + 4f, row.y + 4f, 24f, 24f), def);
            Widgets.Label(new Rect(buyer.xMax + 32f, row.y, reward.x - buyer.xMax - 36f, RowHeight),
                $"{(def?.LabelCap.ToString() ?? order.Label)} x{order.Count}");
            Widgets.Label(reward, "RimMult.MarketReward".Translate(order.Reward, days));
            Text.Anchor = TextAnchor.UpperLeft;

            if (order.Buyer == session.MyOwnerKey)
            {
                if (Widgets.ButtonText(button, "RimMult.MarketCancelOrder".Translate(), active: canDeal) && canDeal)
                    MarketDeals.Cancel(MarketActionKind.CancelOrder, order.Id);
                continue;
            }
            var have = canDeal && def != null ? Goods.CountOf(def) : 0;
            var atWar = order.IsNpc ? MarketDeals.HostileNpc(order.NpcFaction, order.NpcFactionIndex) != null
                : session.RelationWith(order.Buyer) == PlayerRelation.Hostile;
            var can = canDeal && def != null && !atWar && have >= order.Count;
            if (Widgets.ButtonText(button, "RimMult.MarketFulfill".Translate(), active: can) && can)
                Find.WindowStack.Add(Dialog_MessageBox.CreateConfirmation(
                    "RimMult.MarketFulfillConfirm".Translate(def!.LabelCap, order.Count, order.BuyerName, order.Reward), () => MarketDeals.Fulfill(order)));
            if (atWar)
                TooltipHandler.TipRegion(button, "RimMult.MarketAtWar".Translate(order.BuyerName));
            else if (canDeal && def != null && have < order.Count)
                TooltipHandler.TipRegion(button, "RimMult.MarketNotEnough".Translate(def.LabelCap, order.Count, have));
        }
        Widgets.EndScrollView();
    }

    /// <summary>A player in their color; an NPC faction in grey, marked as one.</summary>
    private static string PlayerName(ClientSession session, ulong owner, string name)
    {
        if (NpcTrader.IsNpc(owner))
            return $"<color=#A0A0A0>{"RimMult.MarketNpc".Translate(name)}</color>";
        var color = session.Stats.FirstOrDefault(s => s.Owner == owner)?.ColorIndex ?? 0;
        return PlayerPalette.Colorize(name, color);
    }

    private static void Empty(Rect rect, string key)
    {
        GUI.color = Color.gray;
        Widgets.Label(rect, key.Translate());
        GUI.color = Color.white;
    }
}

/// <summary>Picks goods from the stockpiles and a price, and puts them up for sale.</summary>
internal sealed class Dialog_PostLot : Window
{
    private const float RowHeight = 30f;

    private readonly List<TransferableOneWay> _stock;
    private Vector2 _scroll;
    private int _price;
    private string _priceBuffer = "";
    private string _filter = "";

    public Dialog_PostLot()
    {
        _stock = Goods.HomeStock();
        doCloseX = true;
        forcePause = false;
        absorbInputAroundWindow = true;
        closeOnClickedOutside = false;
        optionalTitle = "RimMult.MarketPostLot".Translate();
    }

    public override Vector2 InitialSize => new(760f, 680f);

    public override void DoWindowContents(Rect inRect)
    {
        Text.Font = GameFont.Small;
        var filterRect = new Rect(inRect.x, inRect.y, inRect.width, 30f);
        _filter = Widgets.TextField(filterRect, _filter);

        var bottom = new Rect(inRect.x, inRect.yMax - 76f, inRect.width, 76f);
        var list = new Rect(inRect.x, filterRect.yMax + 6f, inRect.width, bottom.y - filterRect.yMax - 12f);
        Widgets.DrawMenuSection(list);
        var shown = _stock.Where(t => _filter.Length == 0 || t.LabelCap.ToString().IndexOf(_filter, StringComparison.OrdinalIgnoreCase) >= 0).ToList();
        var inner = list.ContractedBy(4f);
        var view = new Rect(0f, 0f, inner.width - 16f, shown.Count * RowHeight);
        Widgets.BeginScrollView(inner, ref _scroll, view);
        for (var i = 0; i < shown.Count; i++)
        {
            var transferable = shown[i];
            var row = new Rect(0f, i * RowHeight, view.width, RowHeight);
            if (i % 2 == 1)
                Widgets.DrawLightHighlight(row);
            Widgets.ThingIcon(new Rect(row.x, row.y + 3f, 24f, 24f), transferable.AnyThing);
            var adjust = new Rect(row.xMax - 240f, row.y, 240f, row.height);
            Widgets.Label(new Rect(row.x + 30f, row.y + 4f, adjust.x - row.x - 34f, row.height), transferable.LabelCap + $" ({transferable.MaxCount})");
            TransferableUIUtility.DoCountAdjustInterface(adjust, transferable, i, 0, transferable.MaxCount);
        }
        Widgets.EndScrollView();

        var chosen = _stock.Where(t => t.CountToTransfer > 0).ToList();
        var value = chosen.Sum(t => t.AnyThing.MarketValue * t.CountToTransfer);
        var line = "RimMult.MarketLotValue".Translate(value.ToStringMoney()).ToString();
        if (Multiplayer.Session is { } session && session.MarketFeeFor(Mathf.Max(1, _price)) is var fee && fee > 0)
            line += " · " + "RimMult.MarketFee".Translate(fee, session.Market.FreeListings);
        Widgets.Label(new Rect(bottom.x, bottom.y, bottom.width, 30f), line);
        var priceLabel = new Rect(bottom.x, bottom.y + 38f, 180f, 30f);
        Widgets.Label(priceLabel, "RimMult.MarketPriceLabel".Translate());
        Widgets.TextFieldNumeric(new Rect(priceLabel.xMax, priceLabel.y, 140f, 30f), ref _price, ref _priceBuffer, 0, MarketLot.MaxPrice);

        var ok = chosen.Count > 0 && _price > 0;
        if (Widgets.ButtonText(new Rect(bottom.xMax - 220f, priceLabel.y, 220f, 34f), "RimMult.MarketPostLotButton".Translate(), active: ok) && ok
            && MarketDeals.CanDeal(out _))
        {
            MarketDeals.PostLot(chosen, _price);
            Close();
        }
    }
}

/// <summary>Picks an item, a count, a reward and a deadline, and posts the order (the reward leaves now).</summary>
internal sealed class Dialog_PostOrder : Window
{
    private const float RowHeight = 28f;
    private static readonly int[] DayOptions = { 5, 15, 30, 60 };

    private readonly List<ThingDef> _defs;
    private ThingDef? _def;
    private Vector2 _scroll;
    private string _filter = "";
    private int _count = 10;
    private string _countBuffer = "10";
    private int _reward = 100;
    private string _rewardBuffer = "100";
    private int _days = 15;

    public Dialog_PostOrder()
    {
        _defs = DefDatabase<ThingDef>.AllDefsListForReading
            .Where(d => d.category == ThingCategory.Item && !d.IsCorpse && d.BaseMarketValue > 0f && d.tradeability != Tradeability.None && !d.destroyOnDrop)
            .OrderBy(d => d.label)
            .ToList();
        doCloseX = true;
        forcePause = false;
        absorbInputAroundWindow = true;
        closeOnClickedOutside = false;
        optionalTitle = "RimMult.MarketPostOrder".Translate();
    }

    public override Vector2 InitialSize => new(640f, 700f);

    public override void DoWindowContents(Rect inRect)
    {
        Text.Font = GameFont.Small;
        var filterRect = new Rect(inRect.x, inRect.y, inRect.width, 30f);
        _filter = Widgets.TextField(filterRect, _filter);

        var bottom = new Rect(inRect.x, inRect.yMax - 120f, inRect.width, 120f);
        var list = new Rect(inRect.x, filterRect.yMax + 6f, inRect.width, bottom.y - filterRect.yMax - 12f);
        Widgets.DrawMenuSection(list);
        var shown = _defs.Where(d => _filter.Length == 0 || d.label.IndexOf(_filter, StringComparison.OrdinalIgnoreCase) >= 0).ToList();
        var inner = list.ContractedBy(4f);
        var view = new Rect(0f, 0f, inner.width - 16f, shown.Count * RowHeight);
        Widgets.BeginScrollView(inner, ref _scroll, view);
        for (var i = 0; i < shown.Count; i++)
        {
            // Only the rows in sight: there are a lot of items with mods.
            var row = new Rect(0f, i * RowHeight, view.width, RowHeight);
            if (row.yMax < _scroll.y || row.y > _scroll.y + inner.height)
                continue;
            var def = shown[i];
            if (def == _def)
                Widgets.DrawHighlightSelected(row);
            else if (i % 2 == 1)
                Widgets.DrawLightHighlight(row);
            Widgets.ThingIcon(new Rect(row.x, row.y + 2f, 24f, 24f), def);
            Widgets.Label(new Rect(row.x + 30f, row.y + 3f, row.width - 30f, row.height), def.LabelCap);
            if (Widgets.ButtonInvisible(row))
                _def = def;
        }
        Widgets.EndScrollView();

        var what = _def != null
            ? "RimMult.MarketOrderWhat".Translate(_def.LabelCap, (_def.BaseMarketValue * WorldPrices.FactorOf(_def) * _count).ToStringMoney()).ToString()
            : "RimMult.MarketOrderPick".Translate().ToString();
        if (Multiplayer.Session is { } session && session.MarketFeeFor(_reward) is var fee && fee > 0)
            what += " · " + "RimMult.MarketFee".Translate(fee, session.Market.FreeListings);
        Widgets.Label(new Rect(bottom.x, bottom.y, bottom.width, 26f), what);
        var y = bottom.y + 30f;
        Widgets.Label(new Rect(bottom.x, y, 110f, 28f), "RimMult.MarketCountLabel".Translate());
        Widgets.TextFieldNumeric(new Rect(bottom.x + 110f, y, 100f, 28f), ref _count, ref _countBuffer, 1, MarketOrder.MaxCount);
        Widgets.Label(new Rect(bottom.x + 230f, y, 110f, 28f), "RimMult.MarketRewardLabel".Translate());
        Widgets.TextFieldNumeric(new Rect(bottom.x + 340f, y, 120f, 28f), ref _reward, ref _rewardBuffer, 1, MarketLot.MaxPrice);
        y += 34f;
        Widgets.Label(new Rect(bottom.x, y, 110f, 30f), "RimMult.TermsDays".Translate());
        var x = bottom.x + 110f;
        foreach (var days in DayOptions)
        {
            if (Widgets.ButtonText(new Rect(x, y, 70f, 30f), "RimMult.PactDays".Translate(days), active: days != _days))
                _days = days;
            x += 74f;
        }

        var ok = _def != null && _count > 0 && _reward > 0;
        if (Widgets.ButtonText(new Rect(bottom.xMax - 150f, y, 150f, 30f), "RimMult.MarketPostOrderButton".Translate(), active: ok) && ok
            && MarketDeals.CanDeal(out _))
        {
            MarketDeals.PostOrder(_def!, _count, _reward, _days);
            Close();
        }
    }
}
