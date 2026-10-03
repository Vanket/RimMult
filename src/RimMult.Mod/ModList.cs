using System.Collections.Generic;
using System.Linq;
using RimMult.Shared.Mods;
using Verse;

namespace RimMult;

public static class ModList
{
    /// <summary>
    /// Purely visual / interface mods that don't change anything the other players see, enabled as
    /// "client-side" by default. Anyone can mark more in the RimMult mod settings.
    /// </summary>
    public static readonly string[] DefaultClientOnly =
    {
        "m00nl1ght.mappreview",
        "brrainz.cameraplus",
        "jaxe.rimhud",
        "dubwise.dubsmintmenus",
        "dubwise.dubsmintminimap",
    };

    private static List<ModEntry>? _allEntries;

    /// <summary>Running mods in load order, including client-side ones.</summary>
    public static IReadOnlyList<ModContentPack> Running => LoadedModManager.RunningModsListForReading;

    /// <summary>
    /// The mod list compared with other players: running mods in load order, minus the ones this player marked
    /// client-side. Each mod is fingerprinted by its declared version plus the MVIDs of its loaded assemblies,
    /// which change on every rebuild, so an outdated DLL of the same mod is caught too.
    /// </summary>
    public static List<ModEntry> Entries()
    {
        // The running mods can't change while the game runs (changing mods restarts it); compute them once.
        _allEntries ??= Running
            .Select(mod => new ModEntry(mod.PackageId, mod.Name, Fingerprint(mod), WorkshopId(mod)))
            .ToList();
        return _allEntries.Where(entry => !IsClientOnly(entry.PackageId)).ToList();
    }

    public static string ComputeHash() => ModListHash.Compute(Entries());

    /// <summary>Whether this mod is left out of the comparison. RimMult itself and the game always count.</summary>
    public static bool IsClientOnly(string packageId)
    {
        var id = packageId.ToLowerInvariant();
        if (id == "vanket.rimmult" || id.StartsWith("ludeon."))
            return false;
        return RimMultMod.Instance.Settings.ClientOnlyMods.Contains(id);
    }

    public static bool CanBeClientOnly(ModContentPack mod)
    {
        var id = mod.PackageId.ToLowerInvariant();
        return id != "vanket.rimmult" && id != "brrainz.harmony" && !id.StartsWith("ludeon.");
    }

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
