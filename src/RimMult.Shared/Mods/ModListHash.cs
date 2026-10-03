using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using RimMult.Shared.Serialization;

namespace RimMult.Shared.Mods;

public sealed class ModEntry
{
    public ModEntry(string packageId, string name, string fingerprint, ulong workshopId)
    {
        PackageId = packageId;
        Name = name;
        Fingerprint = fingerprint;
        WorkshopId = workshopId;
    }

    public string PackageId { get; }

    /// <summary>Display name, only for showing differences to the player.</summary>
    public string Name { get; }

    /// <summary>Anything that changes when the mod's code changes (assembly MVIDs on the client).</summary>
    public string Fingerprint { get; }

    /// <summary>Steam Workshop item id, or 0 for local mods, Core and DLCs.</summary>
    public ulong WorkshopId { get; }

    public void Write(ByteWriter writer)
    {
        writer.WriteString(PackageId);
        writer.WriteString(Name);
        writer.WriteString(Fingerprint);
        writer.WriteUInt64(WorkshopId);
    }

    public static ModEntry Read(ByteReader reader) => new(
        reader.ReadRequiredString(),
        reader.ReadRequiredString(),
        reader.ReadRequiredString(),
        reader.ReadUInt64());

    public static void WriteList(ByteWriter writer, IReadOnlyList<ModEntry> mods)
    {
        writer.WriteVarUInt((ulong)mods.Count);
        foreach (var mod in mods)
            mod.Write(writer);
    }

    public static List<ModEntry> ReadList(ByteReader reader)
    {
        const int maxMods = 5000;
        var count = reader.ReadVarUInt();
        if (count > maxMods)
            throw new ProtocolException($"Mod list too long: {count}");

        var mods = new List<ModEntry>((int)count);
        for (var i = 0UL; i < count; i++)
            mods.Add(Read(reader));
        return mods;
    }
}

public static class ModListHash
{
    /// <summary>
    /// Order-sensitive hash of the active mod list: load order changes patch order and def overrides,
    /// so two clients with the same mods in a different order must not be treated as compatible.
    /// Names and workshop ids are cosmetic and not hashed.
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
