using System;
using System.Collections.Generic;
using System.Linq;
using RimMult.Shared.Mods;
using Steamworks;
using UnityEngine;
using Verse;

namespace RimMult.UI;

/// <summary>
/// Waits while Steam downloads the host's mods (new subscriptions and newer versions), then goes on: the mod list
/// is written and the game restarts. Steam may sit on an old version for hours unless asked, so it is asked.
/// </summary>
internal sealed class Window_ModSync : Window
{
    /// <summary>Right after asking, Steam may not have flagged an item as outdated yet: don't call it done too early.</summary>
    private const float GraceSeconds = 10f;

    private readonly List<ModEntry> _mods;
    private readonly Action _then;
    private readonly HashSet<ulong> _seenBusy = new();
    private readonly float _startedAt = Time.realtimeSinceStartup;
    private bool _finished;
    private Vector2 _scroll;

    public Window_ModSync(List<ModEntry> mods, Action then)
    {
        _mods = mods;
        _then = then;
        doCloseX = false;
        forcePause = false;
        absorbInputAroundWindow = true;
        closeOnClickedOutside = false;
        closeOnCancel = false;
        onlyOneOfTypeAllowed = true;
        optionalTitle = "RimMult.ModSyncTitle".Translate();
    }

    public override Vector2 InitialSize => new(640f, 480f);

    public override void DoWindowContents(Rect inRect)
    {
        Text.Font = GameFont.Small;
        var hint = new Rect(inRect.x, inRect.y, inRect.width, 50f);
        GUI.color = Color.gray;
        Widgets.Label(hint, "RimMult.ModSyncHint".Translate());
        GUI.color = Color.white;

        var bottom = new Rect(inRect.x, inRect.yMax - 36f, inRect.width, 36f);
        var list = new Rect(inRect.x, hint.yMax + 6f, inRect.width, bottom.y - hint.yMax - 12f);
        Widgets.DrawMenuSection(list);
        var inner = list.ContractedBy(6f);
        var view = new Rect(0f, 0f, inner.width - 16f, _mods.Count * 26f);
        Widgets.BeginScrollView(inner, ref _scroll, view);
        var done = 0;
        for (var i = 0; i < _mods.Count; i++)
        {
            var mod = _mods[i];
            var (finished, status) = Status(mod.WorkshopId);
            if (finished)
                done++;
            var row = new Rect(0f, i * 26f, view.width, 24f);
            Widgets.Label(row.LeftPart(0.7f), mod.Name);
            GUI.color = finished ? Color.green : Color.white;
            Widgets.Label(row.RightPart(0.28f), status);
            GUI.color = Color.white;
        }
        Widgets.EndScrollView();

        if (Widgets.ButtonText(new Rect(bottom.x, bottom.y, 160f, bottom.height), "RimMult.ModSyncCancel".Translate()))
            Close();
        if (Widgets.ButtonText(new Rect(bottom.xMax - 300f, bottom.y, 300f, bottom.height), "RimMult.ModSyncGoOn".Translate(done, _mods.Count)))
            Finish();

        if (done == _mods.Count && Time.realtimeSinceStartup - _startedAt > GraceSeconds)
            Finish();
    }

    private void Finish()
    {
        if (_finished)
            return;
        _finished = true;
        Close();
        _then();
    }

    /// <summary>Whether Steam has the latest version of this item installed, and what to show.</summary>
    private (bool Done, string Status) Status(ulong workshopId)
    {
        var id = new PublishedFileId_t(workshopId);
        EItemState state;
        try
        {
            state = (EItemState)SteamUGC.GetItemState(id);
        }
        catch (Exception)
        {
            return (false, "?");
        }
        var busy = (state & (EItemState.k_EItemStateNeedsUpdate | EItemState.k_EItemStateDownloading | EItemState.k_EItemStateDownloadPending)) != 0;
        if (busy)
        {
            _seenBusy.Add(workshopId);
            SteamUGC.GetItemDownloadInfo(id, out var bytes, out var total);
            return (false, total > 0 ? "RimMult.ModSyncDownloading".Translate((int)(bytes * 100 / total)) : "RimMult.ModSyncQueued".Translate());
        }
        var installed = (state & EItemState.k_EItemStateInstalled) != 0;
        var settled = _seenBusy.Contains(workshopId) || Time.realtimeSinceStartup - _startedAt > GraceSeconds;
        return installed && settled ? (true, "RimMult.ModSyncDone".Translate()) : (false, "RimMult.ModSyncQueued".Translate());
    }
}
