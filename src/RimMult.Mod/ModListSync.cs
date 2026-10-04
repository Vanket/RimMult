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
                count++;
            }
            catch (Exception e)
            {
                Log.Warning($"[RimMult] Could not subscribe to {mod.Name} ({mod.WorkshopId}): {e.Message}");
            }
        }
        return count;
    }

    /// <summary>The host's RimMult build (from its mod list), or null.</summary>
    public static string? HostBuild(IReadOnlyList<ModEntry> server) =>
        server.FirstOrDefault(m => string.Equals(m.PackageId, "vanket.rimmult", StringComparison.OrdinalIgnoreCase))?.Fingerprint.Split('|')[0];

    /// <summary>
    /// "Make everything like the host's": says what will happen (download, update, enable, disable, reorder, what
    /// can't be fixed here) and, once confirmed, does it all with a single restart.
    /// </summary>
    public static void ConfirmFixAll(IReadOnlyList<ModEntry> server, ModListDiff diff)
    {
        var plan = Plan(server);
        var active = ModsConfig.ActiveModsInLoadOrder.ToList();
        var download = plan.Missing.Where(m => m.WorkshopId != 0).ToList();
        // Only a Workshop copy can be brought up to date from here; a local one is the player's own business.
        var update = diff.DifferentVersion.Where(m => m.WorkshopId != 0 && InstalledMod(m.PackageId, active)?.OnSteamWorkshop == true).ToList();
        var cannot = plan.Missing.Where(m => m.WorkshopId == 0).Concat(diff.DifferentVersion.Where(m => !update.Contains(m))).ToList();

        if (!plan.HasChanges && download.Count == 0 && update.Count == 0)
        {
            Find.WindowStack.Add(new Dialog_MessageBox("RimMult.ModsCannotFix".Translate(Names(cannot))));
            return;
        }
        var order = (plan.OrderChanged ? "RimMult.ModsOrderChanged" : "RimMult.ModsOrderSame").Translate();
        Find.WindowStack.Add(Dialog_MessageBox.CreateConfirmation(
            "RimMult.ModsFixAllConfirm".Translate(Names(download), Names(update), plan.Enabled.Count, plan.Disabled.Count, order, Names(cannot)),
            () => FixAll(server, download, update)));
    }

    private static string Names(List<ModEntry> mods) =>
        mods.Count == 0 ? "0" : $"{mods.Count} ({string.Join(", ", mods.Take(6).Select(m => m.Name))}{(mods.Count > 6 ? ", …" : "")})";

    private static void FixAll(IReadOnlyList<ModEntry> server, List<ModEntry> download, List<ModEntry> update)
    {
        Subscribe(download);
        var fetch = download.Concat(update).ToList();
        foreach (var mod in fetch)
        {
            try
            {
                // Steam may keep an old version for hours unless asked: ask now, ahead of everything else.
                SteamUGC.DownloadItem(new PublishedFileId_t(mod.WorkshopId), bHighPriority: true);
            }
            catch (Exception e)
            {
                Log.Warning($"[RimMult] Could not ask Steam for {mod.Name}: {e.Message}");
            }
        }
        if (fetch.Count == 0)
            Apply(Plan(server));
        else
            Find.WindowStack.Add(new UI.Window_ModSync(fetch, () => Apply(Plan(server))));
    }

    private static ModMetaData? InstalledMod(string packageId, List<ModMetaData> active) =>
        active.FirstOrDefault(m => string.Equals(m.PackageIdNonUnique, packageId, StringComparison.OrdinalIgnoreCase))
        ?? ModLister.GetModWithIdentifier(packageId, ignorePostfix: true);

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
