using System.Collections.Generic;
using System.Linq;
using RimMult.Shared.Net;
using RimMult.Shared.Packets;
using RimMult.Shared.World;

namespace RimMult.ServerCore;

/// <summary>
/// Treaties over time: pacts and tributes run out (kept by both: good for their reputation), tributes fall due once
/// a quadrum and the payer is asked to pay (or to refuse, which tears the treaty up).
/// </summary>
public sealed partial class GameServer
{
    /// <summary>Tributes whose payer has been asked for the current payment (asked again on their next visit).</summary>
    private readonly HashSet<long> _dueAsked = new();

    /// <summary>Called with every clock update: expired treaties end, due tributes are asked for.</summary>
    private void CheckTreaties()
    {
        var now = World.Tick;
        var changed = false;
        foreach (var over in Diplomacy.Expire(now))
        {
            changed = true;
            _dueAsked.Remove(over.Id);
            if (over.Kind == TreatyKind.Truce)
                continue;
            StatsOf(over.A).TreatiesKept++;
            StatsOf(over.B).TreatiesKept++;
            Record(ChronicleKind.TreatyExpired, over.A, over.B, text: over.Kind.ToString());
            Broadcast(PacketCodec.Encode(Notice(DiplomacyEvent.TreatyExpired, over.A, over.B, over)), DeliveryMode.ReliableOrdered);
        }

        foreach (var treaty in Diplomacy.Treaties.Where(t => t.PaymentDue(now) && !_dueAsked.Contains(t.Id)).ToList())
        {
            var payer = _sessions.Values.FirstOrDefault(s => s.Player is { InWorld: true } p && OwnerKey(p) == treaty.Payer);
            if (payer == null)
                continue; // asked once they are back
            _dueAsked.Add(treaty.Id);
            Send(payer, Notice(DiplomacyEvent.TributeDue, treaty.Receiver, treaty.Payer, treaty));
        }

        if (!changed)
            return;
        WorldChanged?.Invoke();
        Broadcast(PacketCodec.Encode(WorldUpdatePacket()), DeliveryMode.ReliableOrdered);
    }

    /// <summary>A player came back to the world: their due tributes are asked for again.</summary>
    private void ForgetDueNotices(ulong owner)
    {
        foreach (var treaty in Diplomacy.Treaties.Where(t => t.Payer == owner))
            _dueAsked.Remove(treaty.Id);
    }

    /// <summary>A tribute parcel was sent: if it settles the payment due, the next one is a quadrum away.</summary>
    private void NoteTribute(ulong payer, ulong receiver, long treatyId, int amount)
    {
        var treaty = Diplomacy.Paid(treatyId, payer, amount);
        if (treaty == null || treaty.Receiver != receiver)
            return;
        _dueAsked.Remove(treaty.Id);
        StatsOf(payer).TributePaid += amount;
        Record(ChronicleKind.TributePaid, payer, receiver, a: amount);
        WorldChanged?.Invoke();
        Broadcast(PacketCodec.Encode(WorldUpdatePacket()), DeliveryMode.ReliableOrdered);
        Broadcast(PacketCodec.Encode(Notice(DiplomacyEvent.TributePaid, payer, receiver, treaty)), DeliveryMode.ReliableOrdered);
    }

    private DiplomacyNotice Notice(DiplomacyEvent kind, ulong from, ulong to, Treaty treaty) => new()
    {
        From = from,
        FromName = NameOfOwner(from),
        To = to,
        ToName = NameOfOwner(to),
        Event = kind,
        Terms = new TreatyTerms
        {
            Days = (int)((treaty.EndTick - treaty.StartTick) / Treaty.TicksPerDay),
            Tribute = treaty.Amount,
            ProposerPays = treaty.Payer == from,
        },
        TreatyId = treaty.Id,
    };
}
