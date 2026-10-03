using System.Linq;
using RimMult.Shared.Mods;
using Verse;

namespace RimMult;

public static class ModList
{
    /// <summary>
    /// Hash of the running mods in load order. Each mod is fingerprinted by the MVIDs of its loaded assemblies,
    /// which change on every rebuild, so a client with an outdated DLL of the same mod is caught too.
    /// </summary>
    public static string ComputeHash()
    {
        var entries = LoadedModManager.RunningModsListForReading.Select(mod => new ModEntry(
            mod.PackageId,
            string.Join(",", mod.assemblies.loadedAssemblies.Select(a => a.ManifestModule.ModuleVersionId.ToString()))));
        return ModListHash.Compute(entries);
    }
}
