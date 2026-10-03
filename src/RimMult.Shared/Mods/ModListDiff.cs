using System;
using System.Collections.Generic;
using System.Linq;

namespace RimMult.Shared.Mods;

/// <summary>What a client has to change to match the server's mod list.</summary>
public sealed class ModListDiff
{
    private ModListDiff(List<ModEntry> missing, List<ModEntry> extra, List<ModEntry> differentVersion, bool orderDiffers)
    {
        Missing = missing;
        Extra = extra;
        DifferentVersion = differentVersion;
        OrderDiffers = orderDiffers;
    }

    /// <summary>On the server, not active on the client: enable or subscribe.</summary>
    public IReadOnlyList<ModEntry> Missing { get; }

    /// <summary>Active on the client, not on the server: disable.</summary>
    public IReadOnlyList<ModEntry> Extra { get; }

    /// <summary>Both have it, but the code differs (server's entries): update the mod.</summary>
    public IReadOnlyList<ModEntry> DifferentVersion { get; }

    /// <summary>Same set of mods, but in a different load order.</summary>
    public bool OrderDiffers { get; }

    public bool IsEmpty => Missing.Count == 0 && Extra.Count == 0 && DifferentVersion.Count == 0 && !OrderDiffers;

    public static ModListDiff Compute(IReadOnlyList<ModEntry> server, IReadOnlyList<ModEntry> client)
    {
        var comparer = StringComparer.OrdinalIgnoreCase;
        var clientById = new Dictionary<string, ModEntry>(comparer);
        foreach (var mod in client)
            clientById[mod.PackageId] = mod;
        var serverIds = new HashSet<string>(server.Select(m => m.PackageId), comparer);

        var missing = new List<ModEntry>();
        var different = new List<ModEntry>();
        foreach (var mod in server)
        {
            if (!clientById.TryGetValue(mod.PackageId, out var mine))
                missing.Add(mod);
            else if (mine.Fingerprint != mod.Fingerprint)
                different.Add(mod);
        }

        var extra = client.Where(m => !serverIds.Contains(m.PackageId)).ToList();

        // Load order only matters once the sets match; otherwise fixing the sets comes first.
        var orderDiffers = missing.Count == 0 && extra.Count == 0
                           && !server.Select(m => m.PackageId).SequenceEqual(client.Select(m => m.PackageId), comparer);

        return new ModListDiff(missing, extra, different, orderDiffers);
    }
}
