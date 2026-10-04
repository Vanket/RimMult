using System;
using System.IO;
using System.Linq;
using System.Text;
using RimMult.Coop;
using RimMult.Shared;
using RimMult.Sync;
using UnityEngine;
using Verse;

namespace RimMult;

/// <summary>
/// "Bug report": everything needed to look into a problem in one file — versions, the session, the mod list and
/// the log's errors and RimMult lines — saved next to the saves, with its path copied to the clipboard.
/// </summary>
internal static class Diagnostics
{
    private const int MaxLogLines = 400;

    public static void WriteReport()
    {
        try
        {
            var path = Path.Combine(GenFilePaths.SaveDataFolderPath, $"RimMult_report_{DateTime.Now:yyyyMMdd_HHmmss}.txt");
            File.WriteAllText(path, Build(), new UTF8Encoding(false));
            GUIUtility.systemCopyBuffer = path;
            Messages.Message("RimMult.ReportSaved".Translate(path), RimWorld.MessageTypeDefOf.TaskCompletion, historical: false);
            Application.OpenURL("file://" + Path.GetDirectoryName(path));
        }
        catch (Exception e)
        {
            Log.Error($"[RimMult] Could not write the report: {e}");
        }
    }

    private static string Build()
    {
        var text = new StringBuilder();
        text.AppendLine($"RimMult {RimMultMod.Build}, protocol v{ProtocolInfo.Version}");
        text.AppendLine($"RimWorld {RimWorld.VersionControl.CurrentVersionStringWithRev}, {SystemInfo.operatingSystem}, {SystemInfo.processorType}, {SystemInfo.systemMemorySize} MB");
        text.AppendLine($"Time: {DateTime.Now:yyyy-MM-dd HH:mm:ss}, program state: {Current.ProgramState}");

        var session = Multiplayer.Session;
        text.AppendLine();
        text.AppendLine("== Session");
        if (session == null)
        {
            text.AppendLine("not connected");
        }
        else
        {
            text.AppendLine($"server: {session.ServerName}, state: {session.State}, mode: {session.Mode}, pvp: {session.AllowPvp}, hosting: {Multiplayer.IsHosting}");
            text.AppendLine($"in world: {WorldSync.InWorld}, co-op guest: {CoopGuest.State}, visiting: {CoopGuest.Visiting} (help: {CoopGuest.VisitHelp}), travelling: {PlayerVisit.Travelling}");
            if (session.DisconnectReason != null)
                text.AppendLine($"disconnect reason: {session.DisconnectReason}");
            foreach (var player in session.Players)
                text.AppendLine($"  player {player.Id} {player.Name}{(player.IsHost ? " (host)" : "")}{(player.InWorld ? " in world" : "")}");
        }

        text.AppendLine();
        text.AppendLine("== Mods (load order)");
        var clientOnly = RimMultMod.Instance.Settings.ClientOnlyMods;
        foreach (var mod in LoadedModManager.RunningModsListForReading)
        {
            var meta = mod.ModMetaData;
            var workshop = meta != null && meta.OnSteamWorkshop ? meta.GetPublishedFileId().m_PublishedFileId.ToString() : "-";
            var only = clientOnly.Contains(mod.PackageId.ToLowerInvariant()) ? " [only mine]" : "";
            text.AppendLine($"  {mod.Name} | {mod.PackageIdPlayerFacing} | {meta?.ModVersion} | workshop {workshop}{only}");
        }
        text.AppendLine($"mod list hash: {ModList.ComputeHash()}");

        text.AppendLine();
        text.AppendLine("== Log (errors, warnings and RimMult lines, newest last)");
        var lines = Log.Messages
            .Where(m => m.type != LogMessageType.Message || (m.text?.Contains("[RimMult]") ?? false))
            .Select(m => $"[{m.type}{(m.repeats > 1 ? " x" + m.repeats : "")}] {m.text}")
            .ToList();
        foreach (var line in lines.Skip(Math.Max(0, lines.Count - MaxLogLines)))
            text.AppendLine(line);
        return text.ToString();
    }
}
