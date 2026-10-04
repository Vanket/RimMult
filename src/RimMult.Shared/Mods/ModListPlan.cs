using System;
using System.Collections.Generic;
using System.Linq;

namespace RimMult.Shared.Mods;

/// <summary>What this player's active mod list becomes to match the server's (see <see cref="ModListPlan.LikeServer"/>).</summary>
public sealed class ModListPlan
{
    private ModListPlan(List<string> active, List<ModEntry> missing, List<string> enabled, List<string> disabled, bool orderChanged)
    {
        Active = active;
        Missing = missing;
        Enabled = enabled;
        Disabled = disabled;
        OrderChanged = orderChanged;
    }

    /// <summary>The new active list, in load order (package ids as the game knows the installed mods).</summary>
    public IReadOnlyList<string> Active { get; }

    /// <summary>The server's mods this player doesn't have installed.</summary>
    public IReadOnlyList<ModEntry> Missing { get; }

    public IReadOnlyList<string> Enabled { get; }
    public IReadOnlyList<string> Disabled { get; }

    /// <summary>Mods active before and after load in a different order.</summary>
    public bool OrderChanged { get; }

    public bool HasChanges => Enabled.Count > 0 || Disabled.Count > 0 || OrderChanged;

    /// <summary>
    /// The server's mods in the server's order, as far as they are installed here; the player's own mods kept out of
    /// the comparison (<paramref name="keepLocal"/>: translations, interface…) stay, right after the mod they followed.
    /// </summary>
    /// <param name="server">The server's mod list, in load order.</param>
    /// <param name="current">This player's active mods, in load order.</param>
    /// <param name="installed">The id an installed mod goes by here (a package id may carry a postfix), or null.</param>
    /// <param name="keepLocal">Mods that only this player has and keeps.</param>
    /// <param name="expectInstalled">
    /// Missing mods to put in the list anyway (just subscribed: the game installs them by the next start).
    /// </param>
    public static ModListPlan LikeServer(
        IReadOnlyList<ModEntry> server,
        IReadOnlyList<string> current,
        Func<string, string?> installed,
        Func<string, bool> keepLocal,
        Func<ModEntry, bool>? expectInstalled = null)
    {
        var comparer = StringComparer.OrdinalIgnoreCase;
        var result = new List<string>();
        var missing = new List<ModEntry>();
        foreach (var mod in server)
        {
            var id = installed(mod.PackageId);
            if (id == null)
            {
                missing.Add(mod);
                if (expectInstalled?.Invoke(mod) != true)
                    continue;
                id = mod.PackageId.ToLowerInvariant();
            }
            if (!result.Contains(id, comparer))
                result.Add(id);
        }

        // The player's own mods go back next to the mod they followed (a translation right after its mod).
        for (var i = 0; i < current.Count; i++)
        {
            var id = current[i];
            if (result.Contains(id, comparer) || !keepLocal(id))
                continue;
            var at = 0;
            for (var j = i - 1; j >= 0; j--)
            {
                var before = result.FindIndex(r => comparer.Equals(r, current[j]));
                if (before >= 0)
                {
                    at = before + 1;
                    break;
                }
            }
            result.Insert(at, id);
        }

        var enabled = result.Where(r => !current.Contains(r, comparer)).ToList();
        var disabled = current.Where(c => !result.Contains(c, comparer)).ToList();
        var keptBefore = current.Where(c => result.Contains(c, comparer));
        var keptAfter = result.Where(r => current.Contains(r, comparer));
        var orderChanged = !keptBefore.SequenceEqual(keptAfter, comparer);
        return new ModListPlan(result, missing, enabled, disabled, orderChanged);
    }
}
