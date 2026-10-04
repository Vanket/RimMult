using System.Collections.Generic;
using System.Linq;
using RimMult.ClientCore;
using RimMult.Shared.Packets;
using RimMult.Shared.Trade;
using RimMult.Sync;
using RimMult.Shared.World;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace RimMult.UI;

/// <summary>
/// Trading with another player: pick what you give, see what they give, both accept. From home you give from your
/// stockpiles; from a caravan standing at their colony you give its cargo and animals, and what you buy is loaded
/// into the caravan. Goods for a colony arrive by drop pod. Prisoners can be traded too (they arrive free); at war
/// only silver and prisoners: a ransom.
/// </summary>
internal sealed class Dialog_PlayerTrade : Window
{
    private const float RowHeight = 30f;

    private readonly PlayerTrade _trade;
    private readonly Caravan? _caravan;

    /// <summary>At war with the partner: only silver and prisoners change hands.</summary>
    private readonly bool _ransom;
    private readonly List<TransferableOneWay> _mine = new();
    private string _lastSignature = "";
    private Vector2 _mineScroll;
    private Vector2 _theirsScroll;

    public Dialog_PlayerTrade(PlayerTrade trade)
    {
        _trade = trade;
        doCloseX = true;
        forcePause = false;
        absorbInputAroundWindow = true;
        closeOnClickedOutside = false;
        closeOnAccept = false;
        onlyOneOfTypeAllowed = true;
        optionalTitle = "RimMult.TradeTitle".Translate(trade.PartnerName);
        if (trade.MyCaravanId != 0)
            _caravan = Find.WorldObjects.Caravans.FirstOrDefault(c => c.ID == trade.MyCaravanId && !c.Destroyed);
        _ransom = IsRansom(trade.PartnerId);
        if (_ransom)
            optionalTitle = "RimMult.TradeRansomTitle".Translate(trade.PartnerName);
        _trade.Completed += HandOver;
        CollectMyGoods();
    }

    public override Vector2 InitialSize => new(1000f, 700f);

    /// <summary>Trading with an enemy is a ransom (silver and prisoners only).</summary>
    public static bool IsRansom(int partnerId)
    {
        var session = Multiplayer.Session;
        var partner = session?.Players.FirstOrDefault(p => p.Id == partnerId);
        return partner != null && session!.RelationWith(Multiplayer.OwnerKey(partner)) == PlayerRelation.Hostile;
    }

    /// <summary>After committing, the partner may already be sending their goods: we must stay to send ours.</summary>
    public override bool OnCloseRequest() => !(_trade.State == TradeState.Open && _trade.Locked) && base.OnCloseRequest();

    public override void PostClose()
    {
        base.PostClose();
        _trade.Completed -= HandOver;
        _trade.Cancel(); // no-op once completed or cancelled
    }

    public override void DoWindowContents(Rect inRect)
    {
        Text.Font = GameFont.Small;
        var status = new Rect(inRect.x, inRect.y, inRect.width, RowHeight);
        Widgets.Label(status, StatusText());

        var bottom = new Rect(inRect.x, inRect.yMax - 40f, inRect.width, 40f);
        var body = new Rect(inRect.x, status.yMax + 6f, inRect.width, bottom.y - status.yMax - 12f);
        var left = body.LeftHalf().ContractedBy(4f);
        var right = body.RightHalf().ContractedBy(4f);

        DrawMine(left);
        DrawTheirs(right);
        DrawButtons(bottom);

        if (_trade.State == TradeState.Open && !_trade.Locked)
            PushOfferIfChanged();
    }

    private string StatusText() => _trade.State switch
    {
        TradeState.Inviting => "RimMult.TradeWaitingJoin".Translate(_trade.PartnerName),
        TradeState.Invited => "RimMult.TradeWaitingJoin".Translate(_trade.PartnerName),
        TradeState.Completed => "RimMult.TradeDone".Translate(),
        TradeState.Cancelled => "RimMult.TradeCancelled".Translate(),
        _ when _trade.Locked => "RimMult.TradeLocked".Translate(_trade.PartnerName),
        _ when _trade.TheyAccepted => "RimMult.TradeTheyAccepted".Translate(_trade.PartnerName),
        _ when _ransom => "RimMult.TradeHintRansom".Translate(),
        _ when _caravan != null => "RimMult.TradeHintCaravan".Translate(_caravan.LabelCap),
        _ when _trade.PartnerCaravanId != 0 => "RimMult.TradeHintPartnerCaravan".Translate(_trade.PartnerName),
        _ => "RimMult.TradeHint".Translate(),
    };

    private void DrawMine(Rect rect)
    {
        Widgets.DrawMenuSection(rect);
        var inner = rect.ContractedBy(6f);
        var header = new Rect(inner.x, inner.y, inner.width, RowHeight);
        Text.Font = GameFont.Medium;
        Widgets.Label(header, _caravan != null
            ? "RimMult.TradeCaravanGives".Translate(_trade.MyValue.ToStringMoney())
            : "RimMult.TradeYouGive".Translate(_trade.MyValue.ToStringMoney()));
        Text.Font = GameFont.Small;

        var listRect = new Rect(inner.x, header.yMax, inner.width, inner.height - RowHeight);
        if (_mine.Count == 0)
        {
            GUI.color = Color.gray;
            Widgets.Label(listRect, "RimMult.TradeNoGoods".Translate());
            GUI.color = Color.white;
            return;
        }

        var editable = _trade.State == TradeState.Open && !_trade.Locked;
        var view = new Rect(0f, 0f, listRect.width - 16f, _mine.Count * RowHeight);
        Widgets.BeginScrollView(listRect, ref _mineScroll, view);
        for (var i = 0; i < _mine.Count; i++)
        {
            var transferable = _mine[i];
            var row = new Rect(0f, i * RowHeight, view.width, RowHeight);
            if (i % 2 == 1)
                Widgets.DrawLightHighlight(row);
            Widgets.ThingIcon(new Rect(row.x, row.y + 3f, 24f, 24f), transferable.AnyThing);
            var adjust = new Rect(row.xMax - 240f, row.y, 240f, row.height);
            Widgets.Label(new Rect(row.x + 30f, row.y + 4f, adjust.x - row.x - 34f, row.height), LabelOf(transferable) + $" ({transferable.MaxCount})");
            if (editable)
                TransferableUIUtility.DoCountAdjustInterface(adjust, transferable, i, 0, transferable.MaxCount);
            else
                Widgets.Label(adjust, transferable.CountToTransfer.ToString());
        }
        Widgets.EndScrollView();
    }

    private void DrawTheirs(Rect rect)
    {
        Widgets.DrawMenuSection(rect);
        var inner = rect.ContractedBy(6f);
        var header = new Rect(inner.x, inner.y, inner.width, RowHeight);
        Text.Font = GameFont.Medium;
        Widgets.Label(header, "RimMult.TradeTheyGive".Translate(_trade.PartnerName, _trade.TheirValue.ToStringMoney()));
        Text.Font = GameFont.Small;

        var lines = _trade.TheirOffer;
        var listRect = new Rect(inner.x, header.yMax, inner.width, inner.height - RowHeight);
        var view = new Rect(0f, 0f, listRect.width - 16f, lines.Count * RowHeight);
        Widgets.BeginScrollView(listRect, ref _theirsScroll, view);
        for (var i = 0; i < lines.Count; i++)
        {
            var row = new Rect(0f, i * RowHeight, view.width, RowHeight);
            if (i % 2 == 1)
                Widgets.DrawLightHighlight(row);
            Widgets.Label(row.LeftPart(0.75f), $"{lines[i].Label} x{lines[i].Count}");
            Widgets.Label(row.RightPart(0.25f), lines[i].Value.ToStringMoney());
        }
        Widgets.EndScrollView();
    }

    private void DrawButtons(Rect rect)
    {
        var close = new Rect(rect.xMax - 200f, rect.y, 200f, rect.height);
        if (_trade.IsActive)
        {
            if (!_trade.Locked && Widgets.ButtonText(close, "RimMult.TradeCancel".Translate()))
            {
                _trade.Cancel();
                Close();
            }
        }
        else if (Widgets.ButtonText(close, "CloseButton".Translate()))
        {
            Close();
        }

        if (_trade.State != TradeState.Open || _trade.Locked)
            return;
        var accept = new Rect(rect.x, rect.y, 260f, rect.height);
        var label = _trade.IAccepted ? "RimMult.TradeUnaccept".Translate() : "RimMult.TradeAccept".Translate();
        if (Widgets.ButtonText(accept, label))
            _trade.SetAccepted(!_trade.IAccepted);
    }

    /// <summary>
    /// What we could give, grouped like the pod-loading window: the caravan's cargo and animals, or the stored
    /// items on our home maps.
    /// </summary>
    private void CollectMyGoods()
    {
        bool Allowed(Thing thing) => !_ransom || thing.def == ThingDefOf.Silver;
        bool TradablePrisoner(Pawn pawn) => pawn.IsPrisonerOfColony && PawnTransfer.CanSend(pawn, out _, prisoners: true);

        if (_caravan != null)
        {
            var animals = _caravan.PawnsListForReading.Where(p => !_ransom && !p.RaceProps.Humanlike && PawnTransfer.CanSend(p, out _));
            var prisoners = _caravan.PawnsListForReading.Where(TradablePrisoner);
            foreach (var thing in CaravanInventoryUtility.AllInventoryItems(_caravan).Where(t => Allowed(t) && ThingPackage.CanSend(t))
                         .Concat(animals).Concat(prisoners))
                AddGood(thing);
            _mine.SortBy(t => t.LabelCap.ToString());
            return;
        }

        foreach (var map in Find.Maps.Where(m => m.IsPlayerHome))
        {
            foreach (var thing in map.listerThings.ThingsInGroup(ThingRequestGroup.HaulableEver))
            {
                if (thing.def.category != ThingCategory.Item || !thing.Spawned || !thing.IsInAnyStorage()
                    || thing.IsForbidden(Faction.OfPlayer) || !Allowed(thing) || !ThingPackage.CanSend(thing))
                {
                    continue;
                }

                AddGood(thing);
            }
            foreach (var prisoner in map.mapPawns.PrisonersOfColonySpawned.Where(TradablePrisoner))
                AddGood(prisoner);
        }
        _mine.SortBy(t => t.LabelCap.ToString());
    }

    /// <summary>A row's label; prisoners say so.</summary>
    private static string LabelOf(Transferable transferable) =>
        transferable.AnyThing is Pawn { IsPrisonerOfColony: true }
            ? "RimMult.TradePrisoner".Translate(transferable.LabelCap).ToString()
            : transferable.LabelCap.ToString();

    private void AddGood(Thing thing)
    {
        var transferable = TransferableUtility.TransferableMatching(thing, _mine, TransferAsOneMode.PodsOrCaravanPacking);
        if (transferable == null)
        {
            transferable = new TransferableOneWay();
            _mine.Add(transferable);
        }
        transferable.things.Add(thing);
    }

    /// <summary>Still ours to give: on the map, or still with the caravan.</summary>
    private bool StillAvailable(Thing thing)
    {
        if (thing.Destroyed)
            return false;
        if (_caravan == null)
            return thing.Spawned;
        return thing is Pawn pawn ? _caravan.ContainsPawn(pawn) : thing.holdingOwner != null;
    }

    private void PushOfferIfChanged()
    {
        var lines = _mine
            .Where(t => t.CountToTransfer > 0)
            .Select(t => new TradeLine { Label = LabelOf(t), Count = t.CountToTransfer, Value = t.AnyThing.MarketValue * t.CountToTransfer })
            .ToList();
        var signature = string.Join("|", lines.Select(l => l.Label + "#" + l.Count));
        if (signature == _lastSignature)
            return;
        _lastSignature = signature;
        _trade.SetMyOffer(lines);
    }

    /// <summary>The trade is done: our side leaves as a parcel to the partner's colony.</summary>
    private void HandOver()
    {
        var session = Multiplayer.Session;
        var partner = session?.Players.FirstOrDefault(p => p.Id == _trade.PartnerId);
        var colony = partner == null ? null : session!.Colonies.FirstOrDefault(c => c.OwnerSteamId == Multiplayer.OwnerKey(partner));
        if (colony == null)
        {
            Log.Error($"[RimMult] Trade with {_trade.PartnerName} completed but their colony is unknown; nothing was sent.");
            return;
        }

        // If the partner came with a caravan, what they bought goes into it (their colony if it's gone meanwhile).
        var address = _trade.PartnerCaravanId != 0 ? ParcelAddress.ForCaravan(_trade.PartnerCaravanId, colony.Tile) : colony.Tile;

        // Hand over what is still there: colonists may have used some of it while the trade was negotiated.
        var goods = new List<Thing>();
        foreach (var transferable in _mine.Where(t => t.CountToTransfer > 0))
        {
            var available = transferable.things.Where(StillAvailable).ToList();
            var count = System.Math.Min(transferable.CountToTransfer, available.Sum(t => t.stackCount));
            if (count <= 0)
                continue;
            TransferableUtility.TransferNoSplit(available, count, (thing, n) =>
            {
                if (thing is Pawn pawn)
                {
                    if (_caravan != null)
                    {
                        // A sold pack animal leaves its load with the rest of the caravan, as in vanilla trading.
                        CaravanInventoryUtility.MoveAllInventoryToSomeoneElse(pawn, _caravan.PawnsListForReading);
                        _caravan.RemovePawn(pawn);
                    }
                    goods.Add(pawn);
                }
                else
                {
                    goods.Add(thing.SplitOff(n));
                }
            }, errorIfNotEnoughThings: false);
        }
        if (goods.Count == 0)
            return;

        if (Parcels.Send(colony.OwnerSteamId, colony.OwnerName, address, goods))
        {
            foreach (var thing in goods.Where(t => !t.Destroyed))
            {
                if (thing is Pawn pawn && Find.WorldPawns.Contains(pawn))
                    Find.WorldPawns.RemovePawn(pawn);
                thing.Destroy(DestroyMode.Vanish);
            }
        }
        else if (_caravan != null && !_caravan.Destroyed)
        {
            // Couldn't pack: everything goes back into the caravan.
            foreach (var thing in goods)
                _caravan.AddPawnOrItem(thing, addCarriedPawnToWorldPawnsIfAny: true);
        }
        else
        {
            // Couldn't pack: put the goods back where the colony can find them.
            var map = Find.AnyPlayerHomeMap;
            foreach (var thing in goods)
                GenPlace.TryPlaceThing(thing, DropCellFinder.TradeDropSpot(map), map, ThingPlaceMode.Near);
        }
    }
}
