using System;
using System.Collections.Generic;
using System.Linq;
using RimMult.ClientCore;
using RimMult.Shared.World;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace RimMult.Sync;

/// <summary>A parcel kept in the save: outgoing until handed to the server, incoming until dropped on a map.</summary>
public sealed class ParcelRecord : IExposable
{
    public long Id;
    public ulong FromOwner;
    public ulong ToOwner;
    public string ToName = "";
    public string ToTile = "";
    public string FromName = "";
    public string Summary = "";
    public bool Returned;
    public string Payload = "";

    public void ExposeData()
    {
        Scribe_Values.Look(ref Id, "id");
        Scribe_Values.Look(ref FromOwner, "fromOwner");
        Scribe_Values.Look(ref ToOwner, "toOwner");
        Scribe_Values.Look(ref ToName, "toName", "");
        Scribe_Values.Look(ref ToTile, "toTile", "");
        Scribe_Values.Look(ref FromName, "fromName", "");
        Scribe_Values.Look(ref Summary, "summary", "");
        Scribe_Values.Look(ref Returned, "returned");
        Scribe_Values.Look(ref Payload, "payload", "");
    }
}

/// <summary>
/// Items sent between players' colonies. Outgoing parcels wait in the save until the server has them (so pods that
/// land while offline aren't lost); incoming ones are dropped by pod on the target colony, then acknowledged.
/// </summary>
internal static class Parcels
{
    private const float InboxRetrySeconds = 3f;
    private const int RememberedIds = 1000;

    private static float _lastInboxTry;

    public static void Attach(ClientSession session) => session.ParcelReceived += OnParcel;

    /// <summary>Packs <paramref name="things"/> and sends them. The caller removes the originals afterwards.</summary>
    /// <param name="summary">What the recipient is told; by default a list of the contents.</param>
    /// <param name="quiet">No "parcel sent" message (raids announce themselves).</param>
    /// <param name="allowEmpty">Send even with nothing inside (a raid that nobody came back from still has news).</param>
    public static bool Send(ulong toOwner, string toName, string toTile, List<Thing> things,
        string? summary = null, bool quiet = false, bool allowEmpty = false)
    {
        var comp = RimMultGameComp.Instance;
        if (comp == null || (things.Count == 0 && !allowEmpty))
            return false;

        byte[] payload;
        try
        {
            payload = ThingPackage.Pack(things);
        }
        catch (Exception e)
        {
            Log.Error($"[RimMult] Could not pack a parcel: {e}");
            return false;
        }
        if (payload.Length > MailItem.MaxPayloadBytes)
        {
            Messages.Message("RimMult.ParcelTooLarge".Translate(), MessageTypeDefOf.RejectInput, historical: false);
            return false;
        }

        summary ??= ThingPackage.Summarize(things);
        comp.Outbox.Add(new ParcelRecord
        {
            ToOwner = toOwner,
            ToName = toName,
            ToTile = toTile,
            Summary = summary,
            Payload = Convert.ToBase64String(payload),
        });
        if (!quiet)
            Messages.Message("RimMult.ParcelSent".Translate(toName, summary), MessageTypeDefOf.PositiveEvent);
        return true;
    }

    /// <summary>Called every frame while in the shared world.</summary>
    public static void Update(ClientSession session)
    {
        var comp = RimMultGameComp.Instance;
        if (comp == null || session.State != ClientState.Connected)
            return;

        foreach (var parcel in comp.Outbox)
            session.SendParcel(parcel.ToOwner, parcel.ToTile, parcel.Summary, Convert.FromBase64String(parcel.Payload));
        comp.Outbox.Clear();

        var now = Time.realtimeSinceStartup;
        if (comp.Inbox.Count == 0 || now - _lastInboxTry < InboxRetrySeconds)
            return;
        _lastInboxTry = now;

        foreach (var parcel in comp.Inbox.ToList())
        {
            if (TryDeliver(parcel))
            {
                comp.Inbox.Remove(parcel);
                session.AckParcel(parcel.Id);
            }
        }
    }

    private static void OnParcel(MailItem item)
    {
        var comp = RimMultGameComp.Instance;
        var session = Multiplayer.Session;
        if (comp == null || session == null || !WorldSync.InWorld)
            return;

        // Still waiting for a map to land on: it will be confirmed once dropped.
        if (comp.Inbox.Any(p => p.Id == item.Id))
            return;

        // Already dropped earlier (the acknowledgement was lost): just confirm again.
        if (comp.ReceivedParcels.Contains(item.Id))
        {
            session.AckParcel(item.Id);
            return;
        }

        comp.ReceivedParcels.Add(item.Id);
        if (comp.ReceivedParcels.Count > RememberedIds)
            comp.ReceivedParcels.RemoveAt(0);
        comp.Inbox.Add(new ParcelRecord
        {
            Id = item.Id,
            FromOwner = item.FromOwner,
            ToTile = item.ToTile,
            FromName = item.FromName,
            Summary = item.Summary,
            Returned = item.Returned,
            Payload = Convert.ToBase64String(item.Payload),
        });
        _lastInboxTry = float.NegativeInfinity;
    }

    /// <summary>
    /// Loads the parcel into the caravan it was bought for, or drops it by pod on the colony it was addressed to
    /// (or any home colony).
    /// </summary>
    private static bool TryDeliver(ParcelRecord parcel)
    {
        // Raids: an enemy war party arriving, or our own coming home. A refused raid comes back like any parcel.
        if (!parcel.Returned && ParcelAddress.TryParseRaid(parcel.ToTile, out var raidId, out var arrival, out var raidTile, out var live))
            return PlayerRaids.BeginDefense(parcel, raidId, arrival, raidTile, live);
        if (ParcelAddress.TryParseRaidReturn(parcel.ToTile, out raidId, out raidTile, out var survivors, out var captives))
            return PlayerRaids.DeliverReturn(parcel, raidTile, survivors, captives);
        // An ally's people: coming to help (led by the ally in person), or our own coming back from helping.
        if (!parcel.Returned && ParcelAddress.TryParseHelp(parcel.ToTile, out var helpId, out var helpArrival))
            return PlayerVisits.BeginHelp(parcel, helpId, helpArrival);
        if (ParcelAddress.IsHelpReturn(parcel.ToTile))
            return PlayerVisits.DeliverHelpReturn(parcel);
        // An enemy's missile (or ours, that found nobody to hit).
        if (ParcelAddress.TryParseMissile(parcel.ToTile, out var body, out var warhead, out var launchTile))
            return MissileStrikes.Deliver(parcel, body, warhead, launchTile);
        if (ParcelAddress.TryParseOrbitalStrike(parcel.ToTile, out var permit))
            return OrbitalStrikes.Deliver(parcel, permit);
        // Research an admin finished for this colony.
        if (ParcelAddress.IsAdminResearch(parcel.ToTile))
            return UI.AdminGifts.DeliverResearch(parcel);
        // An ally's research notes: no goods, just progress.
        if (!parcel.Returned && ParcelAddress.TryParseResearch(parcel.ToTile, out var project, out var points))
            return ResearchShare.Deliver(parcel, project, points);

        ParcelAddress.Parse(parcel.ToTile, out var caravanId, out var tileText);
        if (ParcelAddress.IsTribute(parcel.ToTile) || ParcelAddress.IsMarket(parcel.ToTile) || ParcelAddress.IsAdminGift(parcel.ToTile))
            tileText = ""; // tribute, market deals and admins' gifts land at any home colony
        var caravan = caravanId is { } id
            ? Find.WorldObjects.Caravans.FirstOrDefault(c => c.ID == id && !c.Destroyed && c.Faction == Faction.OfPlayer)
            : null;
        if (caravan != null)
            return DeliverToCaravan(parcel, caravan);

        Map? map = null;
        if (PlanetTile.TryParse(tileText, out var tile) && tile.Valid)
            map = Find.Maps.FirstOrDefault(m => m.IsPlayerHome && m.Tile == tile);
        map ??= Find.AnyPlayerHomeMap;
        if (map == null)
            return false; // nowhere to land yet (everyone is travelling); try again later

        List<Thing> things;
        try
        {
            things = ThingPackage.Unpack(Convert.FromBase64String(parcel.Payload));
        }
        catch (Exception e)
        {
            Log.Error($"[RimMult] Could not unpack parcel {parcel.Id} from {parcel.FromName}: {e}");
            Find.LetterStack.ReceiveLetter("RimMult.ParcelBrokenLabel".Translate(), "RimMult.ParcelBrokenText".Translate(parcel.FromName), LetterDefOf.NegativeEvent);
            return true; // acknowledge anyway: retrying a broken parcel forever helps nobody
        }

        if (things.Count == 0)
            return true;
        var spot = DropCellFinder.TradeDropSpot(map);
        DropPodUtility.DropThingsNear(spot, map, things, forbid: false);

        TaggedString label, text;
        if (parcel.Returned && ParcelAddress.IsRaid(parcel.ToTile))
        {
            label = "RimMult.RaidBouncedLabel".Translate();
            text = "RimMult.RaidBouncedText".Translate(ThingPackage.Summarize(things));
        }
        else if (parcel.Returned)
        {
            label = "RimMult.ParcelReturnedLabel".Translate();
            text = "RimMult.ParcelReturnedText".Translate(parcel.Summary);
        }
        else if (ParcelAddress.TryParseMarket(parcel.ToTile, out var market, out var npcFaction, out var npcIndex))
        {
            label = ("RimMult.Market." + market + "Label").Translate(parcel.FromName);
            // What arrived, in this game's words (the server's own summaries are plain English).
            text = ("RimMult.Market." + market + "Text").Translate(parcel.FromName, ThingPackage.Summarize(things));
            // A deal with an NPC faction counts like trading with it here: its goodwill rises.
            if (npcFaction.Length > 0 && NpcLayoutSync.FactionOf(npcFaction, npcIndex) is { } faction && !faction.HostileTo(Faction.OfPlayer))
            {
                var worth = things.Sum(t => t.MarketValue * t.stackCount);
                var goodwill = Mathf.Clamp(Mathf.RoundToInt(worth / 400f), 1, 5);
                if (faction.TryAffectGoodwillWith(Faction.OfPlayer, goodwill, canSendMessage: false, canSendHostilityLetter: false))
                    text += "\n\n" + "RimMult.MarketGoodwill".Translate(faction.Name, goodwill);
            }
        }
        else if (ParcelAddress.IsAdminGift(parcel.ToTile))
        {
            label = "RimMult.AdminGiftLabel".Translate(parcel.FromName);
            text = "RimMult.AdminGiftText".Translate(parcel.FromName, ThingPackage.Summarize(things));
        }
        else if (ParcelAddress.IsTribute(parcel.ToTile))
        {
            label = "RimMult.TributeParcelLabel".Translate(parcel.FromName);
            text = "RimMult.TributeParcelText".Translate(parcel.FromName, parcel.Summary);
        }
        else
        {
            label = "RimMult.ParcelLabel".Translate(parcel.FromName);
            text = "RimMult.ParcelText".Translate(parcel.FromName, parcel.Summary);
        }
        Find.LetterStack.ReceiveLetter(label, text, LetterDefOf.PositiveEvent, new LookTargets(new TargetInfo(spot, map)));
        return true;
    }

    /// <summary>Goods bought by a trading caravan go straight into it (animals join it).</summary>
    private static bool DeliverToCaravan(ParcelRecord parcel, Caravan caravan)
    {
        List<Thing> things;
        try
        {
            things = ThingPackage.Unpack(Convert.FromBase64String(parcel.Payload));
        }
        catch (Exception e)
        {
            Log.Error($"[RimMult] Could not unpack parcel {parcel.Id} from {parcel.FromName}: {e}");
            Find.LetterStack.ReceiveLetter("RimMult.ParcelBrokenLabel".Translate(), "RimMult.ParcelBrokenText".Translate(parcel.FromName), LetterDefOf.NegativeEvent);
            return true;
        }

        foreach (var thing in things)
        {
            caravan.AddPawnOrItem(thing, addCarriedPawnToWorldPawnsIfAny: true);
            if (thing is Pawn pawn && !Find.WorldPawns.Contains(pawn))
                Find.WorldPawns.PassToWorld(pawn);
        }

        Find.LetterStack.ReceiveLetter(
            "RimMult.ParcelToCaravanLabel".Translate(parcel.FromName),
            "RimMult.ParcelToCaravanText".Translate(caravan.LabelCap, parcel.FromName, parcel.Summary),
            LetterDefOf.PositiveEvent,
            new LookTargets(caravan));
        return true;
    }
}
