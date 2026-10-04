using System;
using System.Collections.Generic;
using System.Linq;
using RimMult.Shared.Mods;
using RimMult.Steam;
using RimWorld;
using Steamworks;
using Verse;

namespace RimMult;

/// <summary>
/// "Make my mods like the host's": the server's list (sent with a mod mismatch) becomes this game's active mod list —
/// enabled, disabled and ordered like the host's — then the game restarts and joins the host again. Mods the host has
/// and this player doesn't can be subscribed to on the Steam Workshop from the same screen.
/// </summary>
internal static class ModListSync
{
    /// <summary>Workshop items subscribed from the mismatch screen this session: they take their place in the list in advance.</summary>
    private static readonly HashSet<ulong> Subscribed = new();

    public static ModListPlan Plan(IReadOnlyList<ModEntry> server)
    {
        var active = ModsConfig.ActiveModsInLoadOrder.ToList();
        return ModListPlan.LikeServer(
            server,
            active.Select(m => m.PackageId).ToList(),
            id => Installed(id, active),
            ModList.IsClientOnly,
            mod => mod.WorkshopId != 0 && Subscribed.Contains(mod.WorkshopId));
    }

    /// <summary>The id the game knows an installed mod by (an active copy first: local and Workshop copies get postfixes).</summary>
    private static string? Installed(string packageId, List<ModMetaData> active)
    {
        var match = active.FirstOrDefault(m => string.Equals(m.PackageIdNonUnique, packageId, StringComparison.OrdinalIgnoreCase))
                    ?? ModLister.GetModWithIdentifier(packageId, ignorePostfix: true);
        return match?.PackageId;
    }

    private static IReadOnlyList<ModEntry>? _subscribableFor;
    private static List<ModEntry> _subscribable = new();
    private static float _subscribableAt = float.NegativeInfinity;

    /// <summary>The server's mods that aren't installed here but are on the Workshop (worked out at most once a second: drawn every frame).</summary>
    public static List<ModEntry> Subscribable(IReadOnlyList<ModEntry> server)
    {
        var now = UnityEngine.Time.realtimeSinceStartup;
        if (!ReferenceEquals(server, _subscribableFor) || now - _subscribableAt > 1f)
        {
            _subscribableFor = server;
            _subscribableAt = now;
            _subscribable = Plan(server).Missing.Where(m => m.WorkshopId != 0 && !Subscribed.Contains(m.WorkshopId)).ToList();
        }
        return _subscribable;
    }

    /// <summary>Subscribes to the given Workshop items; Steam downloads them (the game picks them up by the next start).</summary>
    public static int Subscribe(IEnumerable<ModEntry> mods)
    {
        var count = 0;
        foreach (var mod in mods)
        {
            try
            {
                SteamUGC.SubscribeItem(new PublishedFileId_t(mod.WorkshopId));
                Subscribed.Add(mod.WorkshopId);
                _subscribableAt = float.NegativeInfinity;
                count++;
            }
            catch (Exception e)
            {
                Log.Warning($"[RimMult] Could not subscribe to {mod.Name} ({mod.WorkshopId}): {e.Message}");
            }
        }
        return count;
    }

    /// <summary>Writes the new mod list, remembers where to rejoin, and restarts the game.</summary>
    public static void Apply(ModListPlan plan)
    {
        Log.Message($"[RimMult] Mods like the host's: {plan.Active.Count} active, +{plan.Enabled.Count} -{plan.Disabled.Count}, order changed: {plan.OrderChanged}");
        Multiplayer.RememberForRejoin();
        ModsConfig.SetActiveToList(plan.Active.ToList());
        ModsConfig.Save();
        ModsConfig.RestartFromChangedMods();
    }

    /// <summary>After a restart for the mods: back to the same host, once the main menu is up.</summary>
    public static void Update()
    {
        var settings = RimMultMod.Instance.Settings;
        if ((settings.RejoinSteamHost == 0 && settings.RejoinAddress.NullOrEmpty())
            || Current.ProgramState != ProgramState.Entry || LongEventHandler.AnyEventNowOrWaiting || !SteamIntegration.Available)
        {
            return;
        }

        var host = settings.RejoinSteamHost;
        var address = settings.RejoinAddress;
        settings.RejoinSteamHost = 0;
        settings.RejoinAddress = "";
        RimMultMod.Instance.WriteSettings();
        if (Multiplayer.IsActive)
            return;
        Log.Message("[RimMult] Joining the host again after the mod list change.");
        if (host != 0)
        {
            Multiplayer.JoinSteam(host, password: null);
        }
        else if (Multiplayer.JoinAddress(address, password: null, out var error))
        {
            Multiplayer.OpenDialog();
        }
        else
        {
            Messages.Message(error, MessageTypeDefOf.RejectInput, historical: false);
        }
    }
}
