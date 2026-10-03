using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;

namespace RimMult.Shared.Mods;

public readonly struct ModEntry
{
    public ModEntry(string packageId, string fingerprint)
    {
        PackageId = packageId;
        Fingerprint = fingerprint;
    }

    public string PackageId { get; }

    /// <summary>Anything that changes when the mod's code changes (assembly MVIDs on the client).</summary>
    public string Fingerprint { get; }
}

public static class ModListHash
{
    /// <summary>
    /// Order-sensitive hash of the active mod list: load order changes patch order and def overrides,
    /// so two clients with the same mods in a different order must not be treated as compatible.
    /// </summary>
    public static string Compute(IEnumerable<ModEntry> mods)
    {
        var text = new StringBuilder();
        foreach (var mod in mods)
        {
            // Package ids are case-insensitive in RimWorld.
            text.Append(mod.PackageId.ToLowerInvariant()).Append('|').Append(mod.Fingerprint).Append('\n');
        }

        using var sha = SHA256.Create();
        var hash = sha.ComputeHash(Encoding.UTF8.GetBytes(text.ToString()));
        var hex = new StringBuilder(hash.Length * 2);
        foreach (var b in hash)
            hex.Append(b.ToString("x2"));
        return hex.ToString();
    }
}
