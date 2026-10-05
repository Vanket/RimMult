using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using RimMult.ClientCore;
using RimMult.Shared.Packets;
using RimMult.Shared.World;
using RimMult.Sync;
using RimWorld;
using UnityEngine;
using Verse;

namespace RimMult.UI;

/// <summary>
/// The admin panel (the host and admins): items, silver and research for any player of the world (online or not:
/// they arrive like parcels), relations between players, the market, and a pause for everyone. The server checks
/// the rights; nothing of it goes into the chronicle.
/// </summary>
internal sealed class Window_Admin : Window
{
    private const float Row = 32f;

    private ulong _target;
    private ulong _other;
    private int _silver = 1000;
    private string _silverBuffer = "1000";
    private Vector2 _scroll;

    private Window_Admin()
    {
        doCloseX = true;
        draggable = true;
        resizeable = true;
        forcePause = false;
        absorbInputAroundWindow = false;
        closeOnClickedOutside = false;
        preventCameraMotion = false;
        onlyOneOfTypeAllowed = true;
        optionalTitle = "RimMult.AdminTitle".Translate();
        _target = Multiplayer.Session?.MyOwnerKey ?? 0;
    }

    public override Vector2 InitialSize => new(760f, 700f);

    public static void Open()
    {
        if (!Find.WindowStack.TryRemove(typeof(Window_Admin)))
            Find.WindowStack.Add(new Window_Admin());
    }

    /// <summary>Everyone this world knows: online players and owners of colonies, by owner key.</summary>
    public static List<(ulong Owner, string Name)> People(ClientSession session)
    {
        var people = session.Players.Select(p => (Multiplayer.OwnerKey(p), p.Name)).ToList();
        foreach (var colony in session.Colonies)
        {
            if (people.All(p => p.Item1 != colony.OwnerSteamId))
                people.Add((colony.OwnerSteamId, colony.OwnerName));
        }
        return people.OrderBy(p => p.Name).ToList();
    }

    private static string NameOf(ClientSession session, ulong owner) =>
        People(session).FirstOrDefault(p => p.Owner == owner).Name ?? "?";

    public override void DoWindowContents(Rect inRect)
    {
        var session = Multiplayer.Session;
        if (session == null || session.State != ClientState.Connected || !session.IsAdmin)
        {
            Close();
            return;
        }
        Text.Font = GameFont.Small;
        var list = new Listing_Standard();
        var view = new Rect(0f, 0f, inRect.width - 16f, 1100f);
        Widgets.BeginScrollView(inRect, ref _scroll, view);
        list.Begin(view);

        // ---- gifts ----
        Heading(list, "RimMult.AdminGifts");
        var row = list.GetRect(Row);
        Widgets.Label(row.LeftPartPixels(120f), "RimMult.AdminPlayer".Translate());
        PlayerButton(new Rect(row.x + 120f, row.y, 260f, Row - 2f), session, _target, owner => _target = owner);
        row = list.GetRect(Row);
        if (Widgets.ButtonText(row.LeftPartPixels(240f), "RimMult.AdminItems".Translate()))
            Find.WindowStack.Add(new Dialog_AdminItems(_target, NameOf(session, _target)));
        if (Widgets.ButtonText(new Rect(row.x + 250f, row.y, 240f, Row - 2f), "RimMult.AdminResearch".Translate()))
            Find.WindowStack.Add(new Dialog_AdminResearch(_target, NameOf(session, _target)));
        row = list.GetRect(Row);
        Widgets.Label(row.LeftPartPixels(120f), "RimMult.AdminSilver".Translate());
        Widgets.TextFieldNumeric(new Rect(row.x + 120f, row.y, 140f, Row - 2f), ref _silver, ref _silverBuffer, 1, 1_000_000);
        if (Widgets.ButtonText(new Rect(row.x + 270f, row.y, 160f, Row - 2f), "RimMult.AdminGive".Translate()))
            AdminGifts.GiveItems(_target, ThingDefOf.Silver, null, _silver, null);
        list.Gap();

        // ---- relations ----
        Heading(list, "RimMult.AdminRelations");
        row = list.GetRect(Row);
        PlayerButton(new Rect(row.x, row.y, 260f, Row - 2f), session, _target, owner => _target = owner);
        Widgets.Label(new Rect(row.x + 270f, row.y + 4f, 30f, Row), "↔");
        PlayerButton(new Rect(row.x + 300f, row.y, 260f, Row - 2f), session, _other, owner => _other = owner);
        row = list.GetRect(Row);
        var x = row.x;
        foreach (var relation in new[] { PlayerRelation.Neutral, PlayerRelation.Allied, PlayerRelation.Hostile })
        {
            if (Widgets.ButtonText(new Rect(x, row.y, 150f, Row - 2f), DiplomacyUi.Label(relation).CapitalizeFirst()) && _other != 0 && _other != _target)
                Send(new AdminAction { Kind = AdminActionKind.SetRelation, Target = _target, Other = _other, Relation = relation });
            x += 156f;
        }
        if (Widgets.ButtonText(new Rect(x, row.y, 200f, Row - 2f), "RimMult.AdminBreakTreaties".Translate()) && _other != 0)
            Send(new AdminAction { Kind = AdminActionKind.BreakTreaties, Target = _target, Other = _other });
        list.Gap();

        // ---- world ----
        Heading(list, "RimMult.AdminWorld");
        row = list.GetRect(Row);
        if (Widgets.ButtonText(row.LeftPartPixels(240f), "RimMult.AdminPause".Translate()))
            Send(new AdminAction { Kind = AdminActionKind.PauseWorld });
        if (Widgets.ButtonText(new Rect(row.x + 250f, row.y, 240f, Row - 2f), "RimMult.AdminResume".Translate()))
            Send(new AdminAction { Kind = AdminActionKind.ResumeWorld });
        row = list.GetRect(Row);
        if (Widgets.ButtonText(row.LeftPartPixels(240f), "RimMult.AdminResetPrices".Translate()))
            Send(new AdminAction { Kind = AdminActionKind.ResetPrices });
        if (Widgets.ButtonText(new Rect(row.x + 250f, row.y, 240f, Row - 2f), "RimMult.AdminNews".Translate()))
            Send(new AdminAction { Kind = AdminActionKind.MarketNews });
        list.Gap();

        // ---- market ----
        Heading(list, "RimMult.AdminMarket");
        foreach (var lot in session.MarketLots)
        {
            row = list.GetRect(Row - 4f);
            Widgets.Label(row.LeftPartPixels(row.width - 130f), $"{lot.SellerName}: {lot.Summary.Replace("<", "‹")} — {lot.Price}");
            if (Widgets.ButtonText(row.RightPartPixels(120f), "RimMult.AdminRemove".Translate()))
                Send(new AdminAction { Kind = AdminActionKind.RemoveLot, Id = lot.Id });
        }
        foreach (var order in session.MarketOrders)
        {
            row = list.GetRect(Row - 4f);
            Widgets.Label(row.LeftPartPixels(row.width - 130f), $"{order.BuyerName}: {order.Label} x{order.Count} — {order.Reward}");
            if (Widgets.ButtonText(row.RightPartPixels(120f), "RimMult.AdminRemove".Translate()))
                Send(new AdminAction { Kind = AdminActionKind.RemoveOrder, Id = order.Id });
        }
        if (session.MarketLots.Count + session.MarketOrders.Count == 0)
        {
            GUI.color = Color.gray;
            list.Label("RimMult.AdminMarketEmpty".Translate());
            GUI.color = Color.white;
        }

        list.End();
        Widgets.EndScrollView();
    }

    private static void Heading(Listing_Standard list, string key)
    {
        Text.Font = GameFont.Medium;
        list.Label(key.Translate());
        Text.Font = GameFont.Small;
    }

    private static void PlayerButton(Rect rect, ClientSession session, ulong selected, Action<ulong> choose)
    {
        var label = selected == 0 ? "RimMult.AdminChoose".Translate().ToString() : NameOf(session, selected);
        if (Widgets.ButtonText(rect, label))
            Find.WindowStack.Add(new FloatMenu(People(session).Select(p => new FloatMenuOption(p.Name, () => choose(p.Owner))).ToList()));
    }

    private static void Send(AdminAction action)
    {
        Multiplayer.Session?.SendAdmin(action);
        Messages.Message("RimMult.AdminDone".Translate(), MessageTypeDefOf.NeutralEvent, historical: false);
    }
}

/// <summary>Making what an admin gives: new things, packed and sent through the server.</summary>
internal static class AdminGifts
{
    /// <summary>At most this many separate things of a kind that doesn't stack (weapons, apparel…).</summary>
    private const int MaxUnstacked = 100;

    public static void GiveItems(ulong target, ThingDef def, ThingDef? stuff, int count, QualityCategory? quality)
    {
        var session = Multiplayer.Session;
        if (session == null || count <= 0)
            return;
        if (def.stackLimit <= 1)
            count = Math.Min(count, MaxUnstacked);
        var things = new List<Thing>();
        while (count > 0)
        {
            var thing = ThingMaker.MakeThing(def, def.MadeFromStuff ? stuff ?? GenStuff.DefaultStuffFor(def) : null);
            thing.stackCount = Math.Min(count, def.stackLimit);
            count -= thing.stackCount;
            if (quality is { } q)
                thing.TryGetComp<CompQuality>()?.SetQuality(q, ArtGenerationContext.Outsider);
            things.Add(thing);
        }
        var summary = ThingPackage.Summarize(things);
        session.SendAdmin(new AdminAction
        {
            Kind = AdminActionKind.GiveItems,
            Target = target,
            Summary = summary,
            Payload = ThingPackage.Pack(things),
        });
        Messages.Message("RimMult.AdminGiven".Translate(summary), MessageTypeDefOf.PositiveEvent, historical: false);
    }

    public static void GiveResearch(ulong target, IReadOnlyCollection<ResearchProjectDef> projects)
    {
        if (Multiplayer.Session is not { } session || projects.Count == 0)
            return;
        session.SendAdmin(new AdminAction
        {
            Kind = AdminActionKind.GiveResearch,
            Target = target,
            Summary = projects.Count.ToString(),
            Payload = Encoding.UTF8.GetBytes(string.Join("\n", projects.Select(p => p.defName))),
        });
        Messages.Message("RimMult.AdminResearchGiven".Translate(projects.Count), MessageTypeDefOf.PositiveEvent, historical: false);
    }

    /// <summary>Research an admin finished for this colony arrives: the projects are done.</summary>
    public static bool DeliverResearch(ParcelRecord parcel)
    {
        string[] names;
        try
        {
            names = Encoding.UTF8.GetString(Convert.FromBase64String(parcel.Payload)).Split('\n');
        }
        catch (Exception)
        {
            return true;
        }
        var done = new List<string>();
        foreach (var name in names)
        {
            var project = DefDatabase<ResearchProjectDef>.GetNamedSilentFail(name.Trim());
            if (project == null || project.IsFinished)
                continue;
            Find.ResearchManager.FinishProject(project, doCompletionDialog: false, doCompletionLetter: false);
            done.Add(project.LabelCap);
        }
        if (done.Count > 0)
        {
            var shown = string.Join(", ", done.Take(15)) + (done.Count > 15 ? ", …" : "");
            Find.LetterStack.ReceiveLetter("RimMult.AdminResearchLabel".Translate(parcel.FromName),
                "RimMult.AdminResearchText".Translate(parcel.FromName, done.Count, shown), LetterDefOf.PositiveEvent);
        }
        return true;
    }
}

/// <summary>An admin picks an item, its material and quality, and how many.</summary>
internal sealed class Dialog_AdminItems : Window
{
    private const float RowHeight = 28f;

    private readonly ulong _target;
    private readonly List<ThingDef> _defs;
    private ThingDef? _def;
    private ThingDef? _stuff;
    private QualityCategory? _quality;
    private string _filter = "";
    private int _count = 100;
    private string _countBuffer = "100";
    private Vector2 _scroll;

    public Dialog_AdminItems(ulong target, string targetName)
    {
        _target = target;
        _defs = DefDatabase<ThingDef>.AllDefsListForReading
            .Where(d => d.category == ThingCategory.Item && !d.IsCorpse && !d.destroyOnDrop && d.label != null)
            .OrderBy(d => d.label)
            .ToList();
        doCloseX = true;
        forcePause = false;
        absorbInputAroundWindow = true;
        closeOnClickedOutside = false;
        optionalTitle = "RimMult.AdminItemsTitle".Translate(targetName);
    }

    public override Vector2 InitialSize => new(640f, 720f);

    public override void DoWindowContents(Rect inRect)
    {
        Text.Font = GameFont.Small;
        var filterRect = new Rect(inRect.x, inRect.y, inRect.width, 30f);
        _filter = Widgets.TextField(filterRect, _filter);

        var bottom = new Rect(inRect.x, inRect.yMax - 112f, inRect.width, 112f);
        var list = new Rect(inRect.x, filterRect.yMax + 6f, inRect.width, bottom.y - filterRect.yMax - 12f);
        Widgets.DrawMenuSection(list);
        var shown = _defs.Where(d => _filter.Length == 0 || d.label.IndexOf(_filter, StringComparison.OrdinalIgnoreCase) >= 0).ToList();
        var inner = list.ContractedBy(4f);
        var view = new Rect(0f, 0f, inner.width - 16f, shown.Count * RowHeight);
        Widgets.BeginScrollView(inner, ref _scroll, view);
        for (var i = 0; i < shown.Count; i++)
        {
            var row = new Rect(0f, i * RowHeight, view.width, RowHeight);
            if (row.yMax < _scroll.y || row.y > _scroll.y + inner.height)
                continue;
            var def = shown[i];
            if (def == _def)
                Widgets.DrawHighlightSelected(row);
            else if (i % 2 == 1)
                Widgets.DrawLightHighlight(row);
            Widgets.ThingIcon(new Rect(row.x, row.y + 2f, 24f, 24f), def);
            Widgets.Label(new Rect(row.x + 30f, row.y + 3f, row.width - 30f, row.height), def.LabelCap);
            if (Widgets.ButtonInvisible(row))
            {
                _def = def;
                _stuff = def.MadeFromStuff ? GenStuff.DefaultStuffFor(def) : null;
                _quality = def.HasComp(typeof(CompQuality)) ? QualityCategory.Normal : null;
            }
        }
        Widgets.EndScrollView();

        Widgets.Label(new Rect(bottom.x, bottom.y, bottom.width, 26f), _def?.LabelCap ?? "RimMult.MarketOrderPick".Translate());
        var y = bottom.y + 30f;
        Widgets.Label(new Rect(bottom.x, y, 110f, 30f), "RimMult.MarketCountLabel".Translate());
        Widgets.TextFieldNumeric(new Rect(bottom.x + 110f, y, 110f, 30f), ref _count, ref _countBuffer, 1, 1_000_000);
        if (_def is { MadeFromStuff: true } made
            && Widgets.ButtonText(new Rect(bottom.x + 230f, y, 170f, 30f), _stuff?.LabelCap ?? "?"))
        {
            Find.WindowStack.Add(new FloatMenu(GenStuff.AllowedStuffsFor(made)
                .Select(s => new FloatMenuOption(s.LabelCap, () => _stuff = s)).ToList()));
        }
        if (_quality is { } quality && Widgets.ButtonText(new Rect(bottom.x + 410f, y, 170f, 30f), quality.GetLabel().CapitalizeFirst()))
        {
            Find.WindowStack.Add(new FloatMenu(Enum.GetValues(typeof(QualityCategory)).Cast<QualityCategory>()
                .Select(q => new FloatMenuOption(q.GetLabel().CapitalizeFirst(), () => _quality = q)).ToList()));
        }
        y += 40f;
        if (Widgets.ButtonText(new Rect(bottom.xMax - 200f, y, 200f, 34f), "RimMult.AdminGive".Translate(), active: _def != null) && _def != null)
            AdminGifts.GiveItems(_target, _def, _stuff, _count, _quality);
    }
}

/// <summary>An admin finishes research projects for a player: one by one, or everything up to a tech level.</summary>
internal sealed class Dialog_AdminResearch : Window
{
    private const float RowHeight = 26f;

    private readonly ulong _target;
    private readonly List<ResearchProjectDef> _projects;
    private readonly HashSet<ResearchProjectDef> _chosen = new();
    private string _filter = "";
    private Vector2 _scroll;

    public Dialog_AdminResearch(ulong target, string targetName)
    {
        _target = target;
        _projects = DefDatabase<ResearchProjectDef>.AllDefsListForReading
            .Where(p => p.knowledgeCategory == null)
            .OrderBy(p => p.techLevel).ThenBy(p => p.LabelCap.ToString())
            .ToList();
        doCloseX = true;
        forcePause = false;
        absorbInputAroundWindow = true;
        closeOnClickedOutside = false;
        optionalTitle = "RimMult.AdminResearchTitle".Translate(targetName);
    }

    public override Vector2 InitialSize => new(640f, 720f);

    public override void DoWindowContents(Rect inRect)
    {
        Text.Font = GameFont.Small;
        var levels = new Rect(inRect.x, inRect.y, inRect.width, 30f);
        Widgets.Label(levels.LeftPartPixels(150f), "RimMult.AdminUpTo".Translate());
        var x = levels.x + 150f;
        foreach (var level in new[] { TechLevel.Neolithic, TechLevel.Medieval, TechLevel.Industrial, TechLevel.Spacer, TechLevel.Ultra })
        {
            if (Widgets.ButtonText(new Rect(x, levels.y, 92f, 28f), level.ToStringHuman().CapitalizeFirst()))
            {
                foreach (var project in _projects.Where(p => p.techLevel <= level))
                    _chosen.Add(project);
            }
            x += 96f;
        }
        var filterRect = new Rect(inRect.x, levels.yMax + 4f, inRect.width, 30f);
        _filter = Widgets.TextField(filterRect, _filter);

        var bottom = new Rect(inRect.x, inRect.yMax - 40f, inRect.width, 40f);
        var list = new Rect(inRect.x, filterRect.yMax + 6f, inRect.width, bottom.y - filterRect.yMax - 12f);
        Widgets.DrawMenuSection(list);
        var shown = _projects.Where(p => _filter.Length == 0 || p.LabelCap.ToString().IndexOf(_filter, StringComparison.OrdinalIgnoreCase) >= 0).ToList();
        var inner = list.ContractedBy(4f);
        var view = new Rect(0f, 0f, inner.width - 16f, shown.Count * RowHeight);
        Widgets.BeginScrollView(inner, ref _scroll, view);
        for (var i = 0; i < shown.Count; i++)
        {
            var row = new Rect(0f, i * RowHeight, view.width, RowHeight);
            if (row.yMax < _scroll.y || row.y > _scroll.y + inner.height)
                continue;
            var project = shown[i];
            var on = _chosen.Contains(project);
            var was = on;
            Widgets.CheckboxLabeled(row, $"{project.LabelCap} <color=#888888>({project.techLevel.ToStringHuman()})</color>", ref on);
            if (on && !was)
                _chosen.Add(project);
            else if (!on && was)
                _chosen.Remove(project);
        }
        Widgets.EndScrollView();

        if (Widgets.ButtonText(bottom.LeftPartPixels(160f), "RimMult.AdminClear".Translate()))
            _chosen.Clear();
        var give = new Rect(bottom.xMax - 260f, bottom.y, 260f, 36f);
        if (Widgets.ButtonText(give, "RimMult.AdminFinishResearch".Translate(_chosen.Count), active: _chosen.Count > 0) && _chosen.Count > 0)
        {
            AdminGifts.GiveResearch(_target, _chosen.ToList());
            Close();
        }
    }
}
