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
    public static bool Send(ulong toOwner, string toName, string toTile, List<Thing> things)
    {
        var comp = RimMultGameComp.Instance;
        if (comp == null || things.Count == 0)
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

        var summary = ThingPackage.Summarize(things);
        comp.Outbox.Add(new ParcelRecord
        {
            ToOwner = toOwner,
            ToName = toName,
            ToTile = toTile,
            Summary = summary,
            Payload = Convert.ToBase64String(payload),
        });
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
            ToTile = item.ToTile,
            FromName = item.FromName,
            Summary = item.Summary,
            Returned = item.Returned,
            Payload = Convert.ToBase64String(item.Payload),
        });
        _lastInboxTry = float.NegativeInfinity;
    }

    /// <summary>Drops the parcel by pod on the colony it was addressed to (or any home colony).</summary>
    private static bool TryDeliver(ParcelRecord parcel)
    {
        Map? map = null;
        if (PlanetTile.TryParse(parcel.ToTile, out var tile) && tile.Valid)
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

        var spot = DropCellFinder.TradeDropSpot(map);
        DropPodUtility.DropThingsNear(spot, map, things, forbid: false);

        var label = parcel.Returned ? "RimMult.ParcelReturnedLabel".Translate() : "RimMult.ParcelLabel".Translate(parcel.FromName);
        var text = parcel.Returned
            ? "RimMult.ParcelReturnedText".Translate(parcel.Summary)
            : "RimMult.ParcelText".Translate(parcel.FromName, parcel.Summary);
        Find.LetterStack.ReceiveLetter(label, text, LetterDefOf.PositiveEvent, new LookTargets(new TargetInfo(spot, map)));
        return true;
    }
}
