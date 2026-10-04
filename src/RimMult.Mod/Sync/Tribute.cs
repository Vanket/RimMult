using System;
using System.Collections.Generic;
using System.Linq;
using RimMult.ClientCore;
using RimMult.Shared.Packets;
using RimMult.Shared.World;
using RimWorld;
using UnityEngine;
using Verse;

namespace RimMult.Sync;

/// <summary>
/// Paying tribute: when a payment falls due the payer is asked (pay now, later, or refuse and tear the treaty up);
/// the silver leaves the home colonies as a parcel to the receiver, which the server counts as the payment.
/// </summary>
internal static class Tribute
{
    /// <summary>"Later" asks again after this many real seconds.</summary>
    private const float LaterSeconds = 300f;

    /// <summary>Payments due and not answered yet, with when to ask again.</summary>
    private static readonly List<(DiplomacyNotice Notice, float AskAt)> Pending = new();

    public static void Reset() => Pending.Clear();

    /// <summary>Silver lying on this player's home maps (what a tribute can be paid from).</summary>
    public static int SilverAvailable() => Goods.SilverAvailable();

    /// <summary>The server says a payment is due: ask now.</summary>
    public static void Due(DiplomacyNotice notice)
    {
        Pending.RemoveAll(p => p.Notice.TreatyId == notice.TreatyId);
        Pending.Add((notice, 0f));
    }

    /// <summary>Called every frame while in the world: asks about the payments due.</summary>
    public static void Update(ClientSession session)
    {
        if (Pending.Count == 0 || Current.ProgramState != ProgramState.Playing || Find.WindowStack.IsOpen<Dialog_MessageBox>())
            return;
        var now = Time.realtimeSinceStartup;
        var index = Pending.FindIndex(p => p.AskAt <= now);
        if (index < 0)
            return;
        var notice = Pending[index].Notice;
        Pending.RemoveAt(index);
        // The treaty may be gone meanwhile (broken, ended).
        if (!session.Treaties.Any(t => t.Id == notice.TreatyId))
            return;
        Ask(session, notice);
    }

    private static void Ask(ClientSession session, DiplomacyNotice notice)
    {
        var amount = notice.Terms.Tribute;
        var dialog = new Dialog_MessageBox(
            "RimMult.TributeDueText".Translate(notice.FromName, amount, SilverAvailable()),
            "RimMult.TributePay".Translate(), () =>
            {
                if (!Pay(notice))
                    Pending.Add((notice, Time.realtimeSinceStartup + LaterSeconds));
            },
            "RimMult.TributeLater".Translate(), () => Pending.Add((notice, Time.realtimeSinceStartup + LaterSeconds)))
        {
            buttonCText = "RimMult.TributeRefuseBreak".Translate(),
            buttonCAction = () => session.SendDiplomacy(notice.From, DiplomacyAction.BreakTreaty),
            buttonCClose = true,
        };
        Find.WindowStack.Add(dialog);
    }

    /// <summary>Sends the payment; false (and a message) when there isn't enough silver.</summary>
    private static bool Pay(DiplomacyNotice notice)
    {
        var amount = notice.Terms.Tribute;
        var available = SilverAvailable();
        if (available < amount)
        {
            Messages.Message("RimMult.TributeNotEnough".Translate(amount, available), MessageTypeDefOf.RejectInput, historical: false);
            return false;
        }
        var receiver = Multiplayer.Session?.Colonies.FirstOrDefault(c => c.OwnerSteamId == notice.From);
        return SendSilver(notice.From, receiver?.OwnerName ?? notice.FromName, ParcelAddress.ForTribute(notice.TreatyId, amount), amount);
    }

    /// <summary>Takes <paramref name="amount"/> silver off the home maps and sends it; puts it back if that fails.</summary>
    public static bool SendSilver(ulong owner, string name, string address, int amount)
    {
        if (Goods.TakeSilver(amount) is not { } taken)
            return false;
        if (!Parcels.Send(owner, name, address, taken, ThingPackage.Summarize(taken), quiet: true))
        {
            Goods.PutBack(taken);
            return false;
        }
        Goods.Gone(taken);
        return true;
    }
}
