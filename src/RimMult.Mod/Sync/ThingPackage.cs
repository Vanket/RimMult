using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using RimWorld;
using Verse;

namespace RimMult.Sync;

/// <summary>
/// Items packed with the game's own save system, so anything any mod adds (comps, qualities, styles) survives the
/// trip to another player's game. Gzipped XML; IDs are reassigned on arrival because each game numbers its own things.
/// </summary>
internal static class ThingPackage
{
    private const string RootElement = "RimMultParcel";

    /// <summary>What may travel: items, and colonists/colony animals that pass <see cref="PawnTransfer.CanSend"/>.</summary>
    public static bool CanSend(Thing thing) => CanSend(thing, out _);

    public static bool CanSend(Thing thing, out string reason)
    {
        reason = "";
        switch (thing)
        {
            case Corpse:
                reason = "RimMult.ParcelNoCorpses".Translate();
                return false;
            case Pawn pawn:
                return PawnTransfer.CanSend(pawn, out reason);
            default:
                return true;
        }
    }

    /// <summary>Packs things that are leaving this game for good (pawns among them are prepared for the move).</summary>
    public static byte[] Pack(List<Thing> things)
    {
        foreach (var pawn in things.OfType<Pawn>())
            PawnTransfer.PrepareToLeave(pawn);

        var path = TempPath();
        try
        {
            Scribe.saver.InitSaving(path, RootElement);
            try
            {
                Scribe_Collections.Look(ref things, "things", LookMode.Deep);
            }
            finally
            {
                Scribe.saver.FinalizeSaving();
            }
            return Compress(File.ReadAllBytes(path));
        }
        finally
        {
            TryDelete(path);
        }
    }

    /// <param name="welcome">Make arriving pawns members of this colony (false: the caller decides who they are).</param>
    public static List<Thing> Unpack(byte[] payload, bool welcome = true)
    {
        var path = TempPath();
        List<Thing>? things = null;
        try
        {
            File.WriteAllBytes(path, Decompress(payload));
            Scribe.loader.InitLoading(path);
            try
            {
                Scribe_Collections.Look(ref things, "things", LookMode.Deep);
            }
            finally
            {
                Scribe.loader.FinalizeLoading();
            }
        }
        finally
        {
            TryDelete(path);
        }

        var result = things?.Where(t => t != null).ToList() ?? new List<Thing>();
        foreach (var thing in result)
        {
            GiveNewIds(thing);
            if (welcome && thing is Pawn pawn)
                PawnTransfer.WelcomeArrived(pawn);
        }
        return result;
    }

    /// <summary>"Steel x200, Medicine x10, …" — what the letters and messages show.</summary>
    public static string Summarize(IEnumerable<Thing> things)
    {
        var groups = things
            .GroupBy(t => t.LabelNoCount)
            .Select(g => (Label: g.Key, Count: g.Sum(t => t.stackCount)))
            .OrderByDescending(g => g.Count)
            .ToList();
        var shown = groups.Take(8).Select(g => g.Count > 1 ? $"{g.Label} x{g.Count}" : g.Label);
        var text = string.Join(", ", shown);
        return groups.Count > 8 ? text + ", …" : text;
    }

    private static void GiveNewIds(Thing thing)
    {
        if (thing.def.HasThingIDNumber)
        {
            thing.thingIDNumber = -1;
            ThingIDMaker.GiveIDTo(thing);
        }
        if (thing is MinifiedThing minified && minified.InnerThing != null)
            GiveNewIds(minified.InnerThing);

        if (thing is Pawn pawn)
        {
            // A pawn travels with its gear, and its hediffs carry game-wide ids of their own.
            foreach (var gear in PawnGear(pawn).ToList())
                GiveNewIds(gear);
            foreach (var hediff in pawn.health?.hediffSet?.hediffs ?? new List<Hediff>())
                hediff.loadID = Find.UniqueIDsManager.GetNextHediffID();
        }
    }

    private static IEnumerable<Thing> PawnGear(Pawn pawn)
    {
        if (pawn.apparel != null)
            foreach (var apparel in pawn.apparel.WornApparel)
                yield return apparel;
        if (pawn.equipment != null)
            foreach (var equipment in pawn.equipment.AllEquipmentListForReading)
                yield return equipment;
        if (pawn.inventory != null)
            foreach (var item in pawn.inventory.innerContainer)
                yield return item;
        if (pawn.carryTracker != null)
            foreach (var carried in pawn.carryTracker.innerContainer)
                yield return carried;
    }

    private static string TempPath()
    {
        var folder = GenFilePaths.TempFolderPath;
        Directory.CreateDirectory(folder);
        return Path.Combine(folder, $"rimmult_{Guid.NewGuid():N}.xml");
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (IOException)
        {
            // A leftover temp file is harmless.
        }
    }

    private static byte[] Compress(byte[] data)
    {
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionMode.Compress))
            gzip.Write(data, 0, data.Length);
        return output.ToArray();
    }

    private static byte[] Decompress(byte[] data)
    {
        using var input = new GZipStream(new MemoryStream(data), CompressionMode.Decompress);
        using var output = new MemoryStream();
        input.CopyTo(output);
        return output.ToArray();
    }
}
