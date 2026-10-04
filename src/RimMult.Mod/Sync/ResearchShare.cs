using System.Collections.Generic;
using System.Linq;
using RimMult.Shared.World;
using RimWorld;
using UnityEngine;
using Verse;

namespace RimMult.Sync;

/// <summary>
/// Research shared between allies: notes on a finished project give the ally half of its cost as progress. Each
/// project goes to each ally once. The notes travel as an empty parcel addressed <see cref="ParcelAddress.ForResearch"/>.
/// </summary>
internal static class ResearchShare
{
    /// <summary>Share of a project's cost an ally gets.</summary>
    public const float Share = 0.5f;

    private static string Key(ulong owner, ResearchProjectDef project) => owner + "|" + project.defName;

    /// <summary>Finished projects that can be shared (anomaly studies aren't research notes).</summary>
    public static IEnumerable<ResearchProjectDef> Shareable() =>
        DefDatabase<ResearchProjectDef>.AllDefsListForReading
            .Where(p => p.IsFinished && p.knowledgeCategory == null && p.Cost > 0f)
            .OrderBy(p => p.techLevel)
            .ThenBy(p => p.LabelCap.ToString());

    public static bool AlreadyShared(ulong owner, ResearchProjectDef project) =>
        RimMultGameComp.Instance?.SharedResearch.Contains(Key(owner, project)) == true;

    public static int PointsOf(ResearchProjectDef project) => Mathf.Max(1, Mathf.RoundToInt(project.Cost * Share));

    public static bool Send(ulong owner, string ownerName, ResearchProjectDef project)
    {
        var comp = RimMultGameComp.Instance;
        var session = Multiplayer.Session;
        if (comp == null || session == null || AlreadyShared(owner, project))
            return false;
        // Notes need no packing: the address says what and how much.
        session.SendParcel(owner, ParcelAddress.ForResearch(project.defName, PointsOf(project)), project.LabelCap, System.Array.Empty<byte>());
        comp.SharedResearch.Add(Key(owner, project));
        Messages.Message("RimMult.ShareResearchSent".Translate(project.LabelCap, ownerName), MessageTypeDefOf.PositiveEvent, historical: false);
        return true;
    }

    /// <summary>An ally's notes arrived: their points go into the project here.</summary>
    public static bool Deliver(ParcelRecord parcel, string defName, int points)
    {
        var project = DefDatabase<ResearchProjectDef>.GetNamedSilentFail(defName);
        if (project == null)
        {
            Log.Warning($"[RimMult] {parcel.FromName} shared research '{defName}', which this game doesn't have.");
            return true;
        }
        var label = "RimMult.ResearchGiftLabel".Translate(parcel.FromName);
        if (project.IsFinished)
        {
            Find.LetterStack.ReceiveLetter(label, "RimMult.ResearchGiftKnown".Translate(parcel.FromName, project.LabelCap), LetterDefOf.NeutralEvent);
            return true;
        }

        var manager = Find.ResearchManager;
        manager.AddProgress(project, points);
        if (project.ProgressReal >= project.Cost)
        {
            manager.FinishProject(project, doCompletionDialog: false, doCompletionLetter: false);
            Find.LetterStack.ReceiveLetter(label, "RimMult.ResearchGiftDone".Translate(parcel.FromName, project.LabelCap), LetterDefOf.PositiveEvent);
            return true;
        }
        Find.LetterStack.ReceiveLetter(label,
            "RimMult.ResearchGiftText".Translate(parcel.FromName, project.LabelCap, points, project.ProgressPercent.ToStringPercent()),
            LetterDefOf.PositiveEvent);
        return true;
    }
}

/// <summary>Picks a finished project to share with an ally.</summary>
internal sealed class Dialog_ShareResearch : Window
{
    private const float RowHeight = 30f;

    private readonly ulong _owner;
    private readonly string _ownerName;
    private readonly List<ResearchProjectDef> _projects;
    private Vector2 _scroll;
    private string _filter = "";

    public Dialog_ShareResearch(ulong owner, string ownerName)
    {
        _owner = owner;
        _ownerName = ownerName;
        _projects = ResearchShare.Shareable().ToList();
        doCloseX = true;
        forcePause = false;
        absorbInputAroundWindow = true;
        closeOnClickedOutside = true;
        optionalTitle = "RimMult.ShareResearchTitle".Translate(ownerName);
    }

    public override Vector2 InitialSize => new(620f, 640f);

    public override void DoWindowContents(Rect inRect)
    {
        Text.Font = GameFont.Small;
        var hint = "RimMult.ShareResearchHint".Translate().ToString();
        var hintHeight = Text.CalcHeight(hint, inRect.width);
        Widgets.Label(new Rect(inRect.x, inRect.y, inRect.width, hintHeight), hint);
        var filterRect = new Rect(inRect.x, inRect.y + hintHeight + 6f, inRect.width, 30f);
        _filter = Widgets.TextField(filterRect, _filter);

        var shown = _projects
            .Where(p => _filter.Length == 0 || p.LabelCap.ToString().IndexOf(_filter, System.StringComparison.OrdinalIgnoreCase) >= 0)
            .ToList();
        var list = new Rect(inRect.x, filterRect.yMax + 6f, inRect.width, inRect.yMax - filterRect.yMax - 6f);
        if (shown.Count == 0)
        {
            GUI.color = Color.gray;
            Widgets.Label(list, "RimMult.ShareResearchNone".Translate());
            GUI.color = Color.white;
            return;
        }

        var view = new Rect(0f, 0f, list.width - 16f, shown.Count * RowHeight);
        Widgets.BeginScrollView(list, ref _scroll, view);
        for (var i = 0; i < shown.Count; i++)
        {
            var project = shown[i];
            var row = new Rect(0f, i * RowHeight, view.width, RowHeight);
            if (i % 2 == 1)
                Widgets.DrawLightHighlight(row);
            var button = new Rect(row.xMax - 130f, row.y + 2f, 130f, RowHeight - 4f);
            Widgets.Label(new Rect(row.x + 4f, row.y + 4f, button.x - row.x - 8f, RowHeight),
                $"{project.LabelCap} (+{ResearchShare.PointsOf(project)})");
            if (ResearchShare.AlreadyShared(_owner, project))
            {
                GUI.color = Color.gray;
                Widgets.Label(button, "RimMult.ShareResearchShared".Translate());
                GUI.color = Color.white;
            }
            else if (Widgets.ButtonText(button, "RimMult.ShareResearchButton".Translate()))
            {
                ResearchShare.Send(_owner, _ownerName, project);
            }
            TooltipHandler.TipRegion(row, project.description);
        }
        Widgets.EndScrollView();
    }
}
