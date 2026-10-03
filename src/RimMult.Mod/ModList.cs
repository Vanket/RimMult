using System.Collections.Generic;
using System.Linq;
using RimMult.Shared.Mods;
using Verse;

namespace RimMult;

public static class ModList
{
    private static List<ModEntry>? _entries;

    /// <summary>
    /// Running mods in load order. Each mod is fingerprinted by its declared version plus the MVIDs of its loaded
    /// assemblies, which change on every rebuild, so a client with an outdated DLL of the same mod is caught too.
    /// The list cannot change while the game runs (changing mods restarts it), so it is computed once.
    /// </summary>
    public static List<ModEntry> Entries() => _entries ??= LoadedModManager.RunningModsListForReading
        .Select(mod => new ModEntry(mod.PackageId, mod.Name, Fingerprint(mod), WorkshopId(mod)))
        .ToList();

    public static string ComputeHash() => ModListHash.Compute(Entries());

    private static string Fingerprint(ModContentPack mod)
    {
        var assemblies = mod.assemblies.loadedAssemblies.Select(a => a.ManifestModule.ModuleVersionId.ToString());
        return (mod.ModMetaData?.ModVersion ?? "") + "|" + string.Join(",", assemblies);
    }

    private static ulong WorkshopId(ModContentPack mod)
    {
        var meta = mod.ModMetaData;
        return meta != null && meta.OnSteamWorkshop ? meta.GetPublishedFileId().m_PublishedFileId : 0;
    }
}
