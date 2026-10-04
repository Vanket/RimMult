using System;
using System.Collections.Generic;
using System.Linq;
using RimMult.Shared.World;
using RimWorld;
using Verse;

namespace RimMult.Sync;

/// <summary>
/// Goods of the home colonies for deals that don't go through a trade window (the market, orders, tribute): what is
/// in the stockpiles, taking some of it, packing it, and putting it back when the deal falls through.
/// </summary>
internal static class Goods
{
    /// <summary>Stored, usable items on the home maps, grouped like the pod-loading window.</summary>
    public static List<TransferableOneWay> HomeStock(Func<Thing, bool>? filter = null)
    {
        var list = new List<TransferableOneWay>();
        foreach (var thing in StoredItems())
        {
            if (filter != null && !filter(thing))
                continue;
            var transferable = TransferableUtility.TransferableMatching(thing, list, TransferAsOneMode.PodsOrCaravanPacking);
            if (transferable == null)
            {
                transferable = new TransferableOneWay();
                list.Add(transferable);
            }
            transferable.things.Add(thing);
        }
        list.SortBy(t => t.LabelCap.ToString());
        return list;
    }

    private static IEnumerable<Thing> StoredItems() =>
        Find.Maps.Where(m => m.IsPlayerHome)
            .SelectMany(m => m.listerThings.ThingsInGroup(ThingRequestGroup.HaulableEver))
            .Where(t => t.def.category == ThingCategory.Item && t.Spawned && t.IsInAnyStorage()
                        && !t.IsForbidden(Faction.OfPlayer) && ThingPackage.CanSend(t));

    /// <summary>How many of <paramref name="def"/> are in the stockpiles.</summary>
    public static int CountOf(ThingDef def) => StoredItems().Where(t => t.def == def).Sum(t => t.stackCount);

    /// <summary>Takes the chosen counts off the maps (what is still there). The caller packs, then destroys or puts back.</summary>
    public static List<Thing> Take(IEnumerable<TransferableOneWay> chosen)
    {
        var taken = new List<Thing>();
        foreach (var transferable in chosen.Where(t => t.CountToTransfer > 0))
        {
            var available = transferable.things.Where(t => !t.Destroyed && t.Spawned).ToList();
            var count = Math.Min(transferable.CountToTransfer, available.Sum(t => t.stackCount));
            if (count > 0)
                TransferableUtility.TransferNoSplit(available, count, (thing, n) => taken.Add(thing.SplitOff(n)), errorIfNotEnoughThings: false);
        }
        return taken;
    }

    /// <summary>Takes <paramref name="count"/> of <paramref name="def"/> from the stockpiles, or nothing if there isn't enough.</summary>
    public static List<Thing>? TakeOf(ThingDef def, int count)
    {
        var stacks = StoredItems().Where(t => t.def == def).ToList();
        if (stacks.Sum(t => t.stackCount) < count)
            return null;
        return TakeFrom(stacks, count);
    }

    private static IEnumerable<Thing> SilverStacks() =>
        Find.Maps.Where(m => m.IsPlayerHome)
            .SelectMany(m => m.listerThings.ThingsOfDef(ThingDefOf.Silver))
            .Where(t => t.Spawned && !t.IsForbidden(Faction.OfPlayer));

    /// <summary>Silver lying on the home maps.</summary>
    public static int SilverAvailable() => Current.ProgramState == ProgramState.Playing ? SilverStacks().Sum(t => t.stackCount) : 0;

    /// <summary>Takes <paramref name="amount"/> silver off the home maps, or nothing if there isn't enough.</summary>
    public static List<Thing>? TakeSilver(int amount)
    {
        var stacks = SilverStacks().ToList();
        return stacks.Sum(t => t.stackCount) < amount ? null : TakeFrom(stacks, amount);
    }

    private static List<Thing> TakeFrom(List<Thing> stacks, int count)
    {
        var taken = new List<Thing>();
        foreach (var stack in stacks)
        {
            if (count <= 0)
                break;
            var n = Math.Min(count, stack.stackCount);
            taken.Add(stack.SplitOff(n));
            count -= n;
        }
        return taken;
    }

    /// <summary>Packs things for the server; null (and a message) if they can't go.</summary>
    public static byte[]? Pack(List<Thing> things)
    {
        try
        {
            var payload = ThingPackage.Pack(things);
            if (payload.Length <= MailItem.MaxPayloadBytes)
                return payload;
            Messages.Message("RimMult.ParcelTooLarge".Translate(), MessageTypeDefOf.RejectInput, historical: false);
        }
        catch (Exception e)
        {
            Log.Error($"[RimMult] Could not pack goods: {e}");
        }
        return null;
    }

    /// <summary>The goods are on their way: gone from this game.</summary>
    public static void Gone(List<Thing> things)
    {
        foreach (var thing in things.Where(t => !t.Destroyed))
            thing.Destroy(DestroyMode.Vanish);
    }

    /// <summary>The deal fell through: the goods go back where the colony can find them.</summary>
    public static void PutBack(List<Thing> things)
    {
        var map = Find.AnyPlayerHomeMap;
        if (map == null)
            return;
        foreach (var thing in things.Where(t => !t.Destroyed && !t.Spawned))
            GenPlace.TryPlaceThing(thing, DropCellFinder.TradeDropSpot(map), map, ThingPlaceMode.Near);
    }
}
