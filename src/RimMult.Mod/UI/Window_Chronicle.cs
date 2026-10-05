using System.Collections.Generic;
using System.Linq;
using RimMult.ClientCore;
using RimMult.Shared.World;
using RimWorld;
using UnityEngine;
using Verse;

namespace RimMult.UI;

/// <summary>Turns chronicle entries into lines in this player's language, and tells about new ones while playing.</summary>
internal static class ChronicleUi
{
    public static void Attach(ClientSession session) => session.ChronicleAdded += OnAdded;

    /// <summary>One entry as a line, names in their players' colors.</summary>
    public static string Describe(ChronicleEntry entry, ClientSession session)
    {
        var colors = session.Stats.ToDictionary(s => s.Owner, s => s.ColorIndex);
        string Name(ulong owner, string name) => PlayerPalette.Colorize(name, colors.TryGetValue(owner, out var color) ? color : (byte)0);

        if (entry.Kind == ChronicleKind.MarketNews)
            return MarketNews(entry.Text, entry.A);
        var key = "RimMult.Chronicle." + entry.Kind + (entry.Kind == ChronicleKind.RaidLaunched && entry.A == 1 ? "Live" : "");
        if (!key.CanTranslate())
            return $"{entry.Kind}: {entry.ActorName} {entry.TargetName} {entry.Text}";
        // An ended treaty names its kind ("Pact", "Tribute"): in this player's language.
        var text = entry.Kind == ChronicleKind.TreatyExpired && ("RimMult.TreatyKind." + entry.Text).CanTranslate()
            ? ("RimMult.TreatyKind." + entry.Text).Translate().ToString()
            : entry.Text.Replace("<", "‹");
        return key.Translate(
            Name(entry.Actor, entry.ActorName), Name(entry.Target, entry.TargetName),
            entry.A, entry.B, entry.C, text);
    }

    /// <summary>"Medicine is getting dearer: +30 %" — a category's label in this game's language.</summary>
    public static string MarketNews(string category, int percent)
    {
        var label = DefDatabase<ThingCategoryDef>.GetNamedSilentFail(category)?.LabelCap.ToString() ?? category;
        return (percent >= 0 ? "RimMult.Chronicle.MarketNewsUp" : "RimMult.Chronicle.MarketNewsDown").Translate(label, Mathf.Abs(percent));
    }

    /// <summary>The game date of an entry (world time).</summary>
    public static string Date(ChronicleEntry entry) =>
        entry.Tick > 0 ? GenDate.DateFullStringAt(entry.Tick, Vector2.zero) : "";

    private static void OnAdded(IReadOnlyList<ChronicleEntry> entries)
    {
        var session = Multiplayer.Session;
        if (session == null || Current.ProgramState != ProgramState.Playing)
            return;
        var me = session.MyOwnerKey;
        foreach (var entry in entries)
        {
            // News about others only: what concerns this player already comes as letters, and so does diplomacy.
            if (entry.Actor == me || entry.Target == me || entry.Kind is ChronicleKind.WarDeclared or ChronicleKind.PeaceMade
                    or ChronicleKind.AllianceMade or ChronicleKind.AllianceBroken or ChronicleKind.ParcelSent or ChronicleKind.PactMade
                    or ChronicleKind.TreatyBroken or ChronicleKind.TributeAgreed or ChronicleKind.UltimatumRejected
                    or ChronicleKind.TreatyExpired or ChronicleKind.TributePaid)
                continue;
            Messages.Message("RimMult.ChronicleNews".Translate(Describe(entry, session)), MessageTypeDefOf.SilentInput, historical: false);
        }
    }
}

/// <summary>The world's chronicle: what happened, newest first, and how the players stand.</summary>
internal sealed class Window_Chronicle : Window
{
    private const float RowGap = 4f;
    private const float DateWidth = 190f;

    private bool _players;
    private Vector2 _scroll;
    private readonly Dictionary<ChronicleEntry, (float Width, float Height, string Text)> _lines = new();

    private Window_Chronicle()
    {
        doCloseX = true;
        draggable = true;
        resizeable = true;
        forcePause = false;
        absorbInputAroundWindow = false;
        closeOnClickedOutside = false;
        preventCameraMotion = false;
        onlyOneOfTypeAllowed = true;
        optionalTitle = "RimMult.ChronicleTitle".Translate();
    }

    public override Vector2 InitialSize => new(1100f, 640f);

    public static void Open()
    {
        if (!Find.WindowStack.TryRemove(typeof(Window_Chronicle)))
            Find.WindowStack.Add(new Window_Chronicle());
    }

    public override void DoWindowContents(Rect inRect)
    {
        var session = Multiplayer.Session;
        if (session == null || session.State != ClientState.Connected)
        {
            Close();
            return;
        }

        Text.Font = GameFont.Small;
        var tabs = new Rect(inRect.x, inRect.y, inRect.width, 30f);
        if (Widgets.ButtonText(tabs.LeftPartPixels(200f), "RimMult.ChronicleTabLog".Translate(), active: _players))
            _players = false;
        if (Widgets.ButtonText(new Rect(tabs.x + 210f, tabs.y, 200f, tabs.height), "RimMult.ChronicleTabPlayers".Translate(), active: !_players))
            _players = true;

        var body = new Rect(inRect.x, tabs.yMax + 8f, inRect.width, inRect.height - tabs.height - 8f);
        Widgets.DrawMenuSection(body);
        if (_players)
            DrawPlayers(body.ContractedBy(6f), session);
        else
            DrawLog(body.ContractedBy(6f), session);
    }

    private void DrawLog(Rect rect, ClientSession session)
    {
        var entries = session.Chronicle;
        if (entries.Count == 0)
        {
            GUI.color = Color.gray;
            Widgets.Label(rect, "RimMult.ChronicleEmpty".Translate());
            GUI.color = Color.white;
            return;
        }

        var textWidth = rect.width - 16f - DateWidth - 8f;
        var heights = new float[entries.Count];
        var total = 0f;
        for (var i = 0; i < entries.Count; i++)
        {
            heights[i] = Line(entries[i], session, textWidth).Height;
            total += heights[i] + RowGap;
        }

        var view = new Rect(0f, 0f, rect.width - 16f, total);
        Widgets.BeginScrollView(rect, ref _scroll, view);
        var y = 0f;
        // Newest first; only the rows in sight are drawn.
        for (var i = entries.Count - 1; i >= 0; i--)
        {
            var height = heights[i];
            if (y + height >= _scroll.y && y <= _scroll.y + rect.height)
            {
                var entry = entries[i];
                var row = new Rect(0f, y, view.width, height);
                if (i % 2 == 0)
                    Widgets.DrawLightHighlight(row);
                GUI.color = Color.gray;
                Widgets.Label(new Rect(row.x + 4f, row.y, DateWidth, height), ChronicleUi.Date(entry));
                GUI.color = Color.white;
                Widgets.Label(new Rect(row.x + DateWidth + 8f, row.y, textWidth, height), Line(entry, session, textWidth).Text);
            }
            y += height + RowGap;
        }
        Widgets.EndScrollView();
    }

    /// <summary>An entry's text and height, cached (the log can hold a thousand lines).</summary>
    private (float Height, string Text) Line(ChronicleEntry entry, ClientSession session, float width)
    {
        if (_lines.TryGetValue(entry, out var line) && Mathf.Approximately(line.Width, width))
            return (line.Height, line.Text);
        var text = ChronicleUi.Describe(entry, session);
        var height = Mathf.Max(24f, Text.CalcHeight(text, width));
        _lines[entry] = (width, height, text);
        return (height, text);
    }

    private static readonly string[] Columns =
    {
        "RimMult.ChronicleColPlayer", "RimMult.ChronicleColColonies", "RimMult.ChronicleColColonists", "RimMult.ChronicleColWealth",
        "RimMult.ChronicleColRaidsLed", "RimMult.ChronicleColRaidsSuffered", "RimMult.ChronicleColHelps", "RimMult.ChronicleColParcels",
        "RimMult.ChronicleColSettlements", "RimMult.ChronicleColWars", "RimMult.ChronicleColBroken", "RimMult.ChronicleColKept",
        "RimMult.ChronicleColTribute",
    };

    private void DrawPlayers(Rect rect, ClientSession session)
    {
        var stats = session.Stats.OrderByDescending(s => s.Wealth).ToList();
        if (stats.Count == 0)
        {
            GUI.color = Color.gray;
            Widgets.Label(rect, "RimMult.ChronicleEmpty".Translate());
            GUI.color = Color.white;
            return;
        }

        const float rowHeight = 28f;
        var nameWidth = rect.width * 0.2f;
        var cellWidth = (rect.width - 16f - nameWidth) / (Columns.Length - 1);
        float X(int column) => column == 0 ? 0f : nameWidth + (column - 1) * cellWidth;
        float W(int column) => column == 0 ? nameWidth : cellWidth;

        Text.Font = GameFont.Tiny;
        var headerHeight = Columns.Max(c => Text.CalcHeight(c.Translate(), cellWidth - 4f));
        var anchor = Text.Anchor;
        Text.Anchor = TextAnchor.LowerCenter;
        for (var c = 0; c < Columns.Length; c++)
            Widgets.Label(new Rect(rect.x + X(c), rect.y, W(c) - 4f, headerHeight), Columns[c].Translate());
        Text.Font = GameFont.Small;

        var list = new Rect(rect.x, rect.y + headerHeight + 4f, rect.width, rect.height - headerHeight - 4f);
        var view = new Rect(0f, 0f, list.width - 16f, stats.Count * rowHeight);
        Widgets.BeginScrollView(list, ref _scroll, view);
        Text.Anchor = TextAnchor.MiddleCenter;
        for (var i = 0; i < stats.Count; i++)
        {
            var s = stats[i];
            var row = new Rect(0f, i * rowHeight, view.width, rowHeight);
            if (i % 2 == 0)
                Widgets.DrawLightHighlight(row);
            var me = s.Owner == session.MyOwnerKey;
            var name = PlayerPalette.Colorize("■ " + s.Name, s.ColorIndex);
            Text.Anchor = TextAnchor.MiddleLeft;
            var nameRect = new Rect(X(0) + 4f, row.y, W(0) - 4f, rowHeight);
            Widgets.Label(nameRect, me ? $"<b>{name}</b>" : name);
            TooltipHandler.TipRegion(nameRect, Reputation.Inspect(s));
            Text.Anchor = TextAnchor.MiddleCenter;
            var values = new[]
            {
                s.Colonies.ToString(), s.Colonists.ToString(), s.Wealth.ToStringMoney(), s.RaidsLed.ToString(), s.RaidsSuffered.ToString(),
                s.HelpsSent.ToString(), s.ParcelsSent.ToString(), s.SettlementsDestroyed.ToString(), s.WarsDeclared.ToString(),
                s.TreatiesBroken.ToString(), s.TreatiesKept.ToString(), ((float)s.TributePaid).ToStringMoney(),
            };
            for (var c = 1; c < Columns.Length; c++)
                Widgets.Label(new Rect(X(c), row.y, W(c), rowHeight), values[c - 1]);
        }
        Text.Anchor = anchor;
        Widgets.EndScrollView();
    }
}
