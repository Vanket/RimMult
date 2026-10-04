using System;
using System.Collections.Generic;
using System.Linq;
using RimMult.ClientCore;
using RimMult.Shared.Packets;
using RimMult.Shared.World;
using RimMult.UI;
using RimWorld;
using UnityEngine;
using Verse;

namespace RimMult.Sync;

/// <summary>
/// Diplomacy with other players: the "Diplomacy" button on their colonies, how relations and treaties are shown, and
/// what this player is told when something happens (a war declared on them, an offer or an ultimatum to answer, …).
/// </summary>
[StaticConstructorOnStartup]
internal static class DiplomacyUi
{
    /// <summary>Pact lengths offered (game days).</summary>
    private static readonly int[] PactDays = { 10, 30, 60, 120 };

    /// <summary>Tribute lengths offered (game days): at least one quadrum, one payment each.</summary>
    private static readonly int[] TributeDays = { 15, 30, 60, 120 };

    private static Texture2D? _icon;

    private static Texture2D Icon => _icon ??=
        ContentFinder<Texture2D>.Get("UI/Commands/CallAid", reportFailure: false)
        ?? ContentFinder<Texture2D>.Get("UI/Commands/FormCaravan", reportFailure: false)
        ?? BaseContent.BadTex;

    public static void Attach(ClientSession session) => session.DiplomacyReceived += OnNotice;

    public static string Label(PlayerRelation relation) => ("RimMult.Relation" + relation).Translate();

    /// <summary>The relation as a colored tag for lists: red war, green alliance, nothing when neutral.</summary>
    public static string Tag(PlayerRelation relation) => relation switch
    {
        PlayerRelation.Hostile => $"<color=#E05050>[{Label(relation)}]</color>",
        PlayerRelation.Allied => $"<color=#60C060>[{Label(relation)}]</color>",
        _ => "",
    };

    /// <summary>The world's time here: this game's clock while playing, else what the server last said.</summary>
    public static long Now(ClientSession session) =>
        Current.ProgramState == ProgramState.Playing && Find.TickManager != null ? Find.TickManager.TicksAbs : session.WorldTick;

    /// <summary>A treaty as one line, from this player's side: "Pact: 12 more days", "You pay 300 silver a quadrum…".</summary>
    public static string TreatyLine(Treaty treaty, ClientSession session, string otherName)
    {
        var days = Mathf.Max(0, Mathf.CeilToInt((treaty.EndTick - Now(session)) / (float)Treaty.TicksPerDay));
        return treaty.Kind switch
        {
            TreatyKind.Pact => "RimMult.TreatyPact".Translate(days),
            TreatyKind.Truce => "RimMult.TreatyTruce".Translate(days),
            _ when treaty.Payer == session.MyOwnerKey => "RimMult.TreatyTributeYouPay".Translate(treaty.Amount, days, otherName),
            _ => "RimMult.TreatyTributeTheyPay".Translate(treaty.Amount, days, otherName),
        };
    }

    /// <summary>The "Diplomacy" button on another player's colony.</summary>
    public static Command_Action Gizmo(ulong owner, string ownerName)
    {
        var session = Multiplayer.Session;
        var command = new Command_Action
        {
            defaultLabel = "RimMult.Diplomacy".Translate(),
            defaultDesc = "RimMult.DiplomacyDesc".Translate(ownerName),
            icon = Icon,
            action = () => Find.WindowStack.Add(new FloatMenu(Options(owner, ownerName))),
        };
        if (session == null || session.State != ClientState.Connected)
            command.Disable("RimMult.TradeNotInWorld".Translate());
        return command;
    }

    private static List<FloatMenuOption> Options(ulong owner, string name)
    {
        var options = new List<FloatMenuOption>();
        var session = Multiplayer.Session;
        if (session == null)
            return options;

        // What binds the two right now, as lines that can't be clicked.
        var treaties = session.TreatiesWith(owner).ToList();
        foreach (var treaty in treaties)
            options.Add(new FloatMenuOption(TreatyLine(treaty, session, name), null));

        var online = session.Players.Any(p => Multiplayer.OwnerKey(p) == owner);
        FloatMenuOption Proposal(string label, Action action) => online
            ? new FloatMenuOption(label, action)
            : new FloatMenuOption(label + " (" + "RimMult.DiplomacyOffline".Translate(name) + ")", null);

        switch (session.RelationWith(owner))
        {
            case PlayerRelation.Neutral:
                if (!session.AllowPvp)
                    options.Add(new FloatMenuOption("RimMult.DeclareWar".Translate() + " (" + "RimMult.RaidPvpOff".Translate() + ")", null));
                else if (treaties.Count > 0)
                    options.Add(new FloatMenuOption("RimMult.DeclareWar".Translate() + " (" + "RimMult.WarBlockedByTreaty".Translate() + ")", null));
                else
                    options.Add(new FloatMenuOption("RimMult.DeclareWar".Translate(), () => Confirm("RimMult.DeclareWarConfirm".Translate(name), () => Send(owner, DiplomacyAction.DeclareWar))));
                options.Add(Proposal("RimMult.ProposeAlliance".Translate(), () => Send(owner, DiplomacyAction.ProposeAlliance)));
                if (treaties.Count == 0)
                {
                    options.Add(Proposal("RimMult.ProposePact".Translate(), () => Find.WindowStack.Add(new FloatMenu(PactDays
                        .Select(days => new FloatMenuOption("RimMult.PactDays".Translate(days),
                            () => Send(owner, DiplomacyAction.ProposePact, new TreatyTerms { Days = days })))
                        .ToList()))));
                    if (session.AllowPvp)
                        options.Add(Proposal("RimMult.DemandTribute".Translate(), () => Find.WindowStack.Add(new Dialog_TreatyTerms(
                            "RimMult.DemandTributeTitle".Translate(name), "RimMult.DemandTributeHint".Translate(name), TributeDays,
                            terms => Send(owner, DiplomacyAction.DemandTribute, terms)))));
                }
                else
                {
                    options.Add(new FloatMenuOption("RimMult.BreakTreaty".Translate(), () => Confirm("RimMult.BreakTreatyConfirm".Translate(name), () => Send(owner, DiplomacyAction.BreakTreaty))));
                }
                break;
            case PlayerRelation.Hostile:
                options.Add(Proposal("RimMult.PeacePlain".Translate(), () => Send(owner, DiplomacyAction.ProposePeace)));
                options.Add(Proposal("RimMult.PeaceIPay".Translate(), () => Find.WindowStack.Add(new Dialog_TreatyTerms(
                    "RimMult.PeaceIPayTitle".Translate(name), "RimMult.PeaceIPayHint".Translate(name), TributeDays,
                    terms =>
                    {
                        terms.ProposerPays = true;
                        Send(owner, DiplomacyAction.ProposePeace, terms);
                    }))));
                options.Add(Proposal("RimMult.PeaceTheyPay".Translate(), () => Find.WindowStack.Add(new Dialog_TreatyTerms(
                    "RimMult.PeaceTheyPayTitle".Translate(name), "RimMult.PeaceTheyPayHint".Translate(name), TributeDays,
                    terms => Send(owner, DiplomacyAction.ProposePeace, terms)))));
                break;
            case PlayerRelation.Allied:
                if (WorldSync.InWorld)
                    options.Add(new FloatMenuOption("RimMult.ShareResearch".Translate(), () => Find.WindowStack.Add(new Dialog_ShareResearch(owner, name))));
                options.Add(new FloatMenuOption("RimMult.BreakAlliance".Translate(), () => Confirm("RimMult.BreakAllianceConfirm".Translate(name), () => Send(owner, DiplomacyAction.BreakAlliance))));
                break;
        }
        return options;
    }

    private static void Send(ulong owner, DiplomacyAction action, TreatyTerms? terms = null)
    {
        Multiplayer.Session?.SendDiplomacy(owner, action, terms);
        if (action is DiplomacyAction.ProposePeace or DiplomacyAction.ProposeAlliance or DiplomacyAction.ProposePact or DiplomacyAction.DemandTribute)
            Messages.Message("RimMult.ProposalSent".Translate(), MessageTypeDefOf.NeutralEvent, historical: false);
    }

    private static void Confirm(string text, Action action) =>
        Find.WindowStack.Add(Dialog_MessageBox.CreateConfirmation(text, action, destructive: true));

    private static void Ask(string text, string accept, Action onAccept, string decline, Action onDecline) =>
        Find.WindowStack.Add(new Dialog_MessageBox(text, accept, onAccept, decline, onDecline));

    private static void OnNotice(DiplomacyNotice notice)
    {
        var session = Multiplayer.Session;
        if (session == null || Find.WindowStack == null)
            return;
        var me = session.MyOwnerKey;
        var toMe = notice.To == me;
        var byMe = notice.From == me;
        var other = toMe ? notice.FromName : notice.ToName;
        var terms = notice.Terms;
        void Answer(DiplomacyAction action) => session.SendDiplomacy(notice.From, action);

        switch (notice.Event)
        {
            case DiplomacyEvent.PeaceProposed when toMe && terms.Tribute > 0:
                Ask("RimMult.PeaceTributeProposedText".Translate(notice.FromName, terms.Days, terms.ProposerPays ? notice.FromName : "RimMult.You".Translate().ToString(), terms.Tribute),
                    "RimMult.DiplomacyAccept".Translate(), () => Answer(DiplomacyAction.Accept),
                    "RimMult.DiplomacyDecline".Translate(), () => Answer(DiplomacyAction.Decline));
                break;
            case DiplomacyEvent.PeaceProposed or DiplomacyEvent.AllianceProposed when toMe:
                var peace = notice.Event == DiplomacyEvent.PeaceProposed;
                Ask((peace ? "RimMult.PeaceProposedText" : "RimMult.AllianceProposedText").Translate(notice.FromName),
                    "RimMult.DiplomacyAccept".Translate(), () => Answer(DiplomacyAction.Accept),
                    "RimMult.DiplomacyDecline".Translate(), () => Answer(DiplomacyAction.Decline));
                break;
            case DiplomacyEvent.PactProposed when toMe:
                Ask("RimMult.PactProposedText".Translate(notice.FromName, terms.Days),
                    "RimMult.DiplomacyAccept".Translate(), () => Answer(DiplomacyAction.Accept),
                    "RimMult.DiplomacyDecline".Translate(), () => Answer(DiplomacyAction.Decline));
                break;
            case DiplomacyEvent.TributeDemanded when toMe:
                Ask("RimMult.TributeDemandedText".Translate(notice.FromName, terms.Days, terms.Tribute, Tribute.SilverAvailable()),
                    "RimMult.TributeAccept".Translate(), () => Answer(DiplomacyAction.Accept),
                    "RimMult.TributeRefuse".Translate(), () => Answer(DiplomacyAction.Decline));
                break;
            case DiplomacyEvent.ProposalDeclined when toMe:
                Messages.Message("RimMult.ProposalDeclined".Translate(notice.FromName), MessageTypeDefOf.NegativeEvent, historical: false);
                break;
            case DiplomacyEvent.TributeDue when toMe:
                Tribute.Due(notice);
                break;
            case DiplomacyEvent.TributePaid when toMe || byMe:
                Messages.Message((byMe ? "RimMult.TributePaidByYou" : "RimMult.TributePaidToYou").Translate(other, terms.Tribute),
                    MessageTypeDefOf.NeutralEvent, historical: false);
                break;
            case DiplomacyEvent.WarDeclared when toMe:
                Letter("RimMult.WarOnYouLabel".Translate(notice.FromName), "RimMult.WarOnYouText".Translate(notice.FromName), LetterDefOf.ThreatBig);
                break;
            case DiplomacyEvent.TreatyBroken when byMe:
                Messages.Message("RimMult.TreatyBrokenByYou".Translate(notice.ToName), MessageTypeDefOf.NeutralEvent, historical: false);
                break;
            case DiplomacyEvent.PeaceMade when toMe || byMe:
                var text = "RimMult.DiplomacyPeaceMadeText".Translate(other) + "\n\n" + (terms.Tribute > 0
                    ? "RimMult.PeaceTributeNote".Translate(PayerName(notice, terms, session), terms.Tribute, terms.Days)
                    : "RimMult.PeaceTruceNote".Translate(DiplomacyBook.TruceDays));
                Letter("RimMult.DiplomacyPeaceMadeLabel".Translate(other), text, LetterDefOf.PositiveEvent);
                break;
            case DiplomacyEvent.TributeAgreed or DiplomacyEvent.UltimatumRejected when toMe || byMe:
                // From: the one who answered the ultimatum (and pays, or refused); to: the one who made it.
                var side = byMe ? "Answered" : "Demanded";
                var key = "RimMult.Diplomacy" + notice.Event + side;
                Letter((key + "Label").Translate(other), (key + "Text").Translate(other, terms.Days, terms.Tribute),
                    notice.Event == DiplomacyEvent.UltimatumRejected ? LetterDefOf.ThreatBig : LetterDefOf.NeutralEvent);
                break;
            case DiplomacyEvent.AllianceMade or DiplomacyEvent.AllianceBroken or DiplomacyEvent.PactMade or DiplomacyEvent.TreatyBroken
                or DiplomacyEvent.TreatyExpired when toMe || byMe:
                var letterKey = "RimMult.Diplomacy" + notice.Event;
                Letter((letterKey + "Label").Translate(other), (letterKey + "Text").Translate(other, terms.Days, terms.Tribute),
                    notice.Event is DiplomacyEvent.AllianceBroken or DiplomacyEvent.TreatyBroken ? LetterDefOf.NegativeEvent : LetterDefOf.PositiveEvent);
                break;
            case DiplomacyEvent.WarDeclared or DiplomacyEvent.PeaceMade or DiplomacyEvent.AllianceMade or DiplomacyEvent.AllianceBroken
                or DiplomacyEvent.PactMade or DiplomacyEvent.TreatyBroken or DiplomacyEvent.TributeAgreed or DiplomacyEvent.UltimatumRejected
                or DiplomacyEvent.TreatyExpired:
                // Between two other players, or our own declaration: a line is enough.
                Messages.Message(("RimMult.DiplomacyNews" + notice.Event).Translate(notice.FromName, notice.ToName), MessageTypeDefOf.NeutralEvent, historical: false);
                break;
        }
    }

    /// <summary>Who pays the tribute that came with a peace: the proposer, or the one who accepted.</summary>
    private static string PayerName(DiplomacyNotice notice, TreatyTerms terms, ClientSession session)
    {
        // The notice comes from the one who accepted; the proposer is the other one.
        var payer = terms.ProposerPays ? notice.To : notice.From;
        return payer == session.MyOwnerKey ? "RimMult.You".Translate().ToString() : payer == notice.From ? notice.FromName : notice.ToName;
    }

    /// <summary>A letter while playing; in the main menu (no letter stack) a message.</summary>
    private static void Letter(string label, string text, LetterDef def)
    {
        if (Current.ProgramState == ProgramState.Playing)
            Find.LetterStack.ReceiveLetter(label, text, def);
        else
            Messages.Message(label, MessageTypeDefOf.NeutralEvent, historical: false);
    }
}

/// <summary>The terms of a tribute: how much silver a quadrum, for how long.</summary>
internal sealed class Dialog_TreatyTerms : Window
{
    private const int MaxSilver = 10_000;

    private readonly string _title;
    private readonly string _hint;
    private readonly int[] _dayOptions;
    private readonly Action<TreatyTerms> _onAccept;
    private float _tribute = 500f;
    private int _days;

    public Dialog_TreatyTerms(string title, string hint, int[] dayOptions, Action<TreatyTerms> onAccept)
    {
        _title = title;
        _hint = hint;
        _dayOptions = dayOptions;
        _days = dayOptions[Mathf.Min(1, dayOptions.Length - 1)];
        _onAccept = onAccept;
        doCloseX = true;
        forcePause = false;
        absorbInputAroundWindow = true;
        closeOnClickedOutside = true;
    }

    public override Vector2 InitialSize => new(520f, 330f);

    public override void DoWindowContents(Rect inRect)
    {
        Text.Font = GameFont.Medium;
        Widgets.Label(new Rect(inRect.x, inRect.y, inRect.width, 34f), _title);
        Text.Font = GameFont.Small;
        var y = inRect.y + 40f;
        var hintHeight = Text.CalcHeight(_hint, inRect.width);
        Widgets.Label(new Rect(inRect.x, y, inRect.width, hintHeight), _hint);
        y += hintHeight + 12f;

        Widgets.Label(new Rect(inRect.x, y, inRect.width, 24f), "RimMult.TermsTribute".Translate(Mathf.RoundToInt(_tribute)));
        y += 26f;
        _tribute = Widgets.HorizontalSlider(new Rect(inRect.x, y, inRect.width, 24f), _tribute, 50f, MaxSilver, roundTo: 50f);
        y += 34f;

        Widgets.Label(new Rect(inRect.x, y, 120f, 30f), "RimMult.TermsDays".Translate());
        var x = inRect.x + 120f;
        foreach (var days in _dayOptions)
        {
            if (Widgets.ButtonText(new Rect(x, y, 80f, 30f), "RimMult.PactDays".Translate(days), active: days != _days))
                _days = days;
            x += 86f;
        }

        var buttons = new Rect(inRect.x, inRect.yMax - 36f, inRect.width, 36f);
        if (Widgets.ButtonText(buttons.LeftHalf().ContractedBy(4f, 0f), "RimMult.TermsSend".Translate()))
        {
            _onAccept(new TreatyTerms { Days = _days, Tribute = Mathf.RoundToInt(_tribute) });
            Close();
        }
        if (Widgets.ButtonText(buttons.RightHalf().ContractedBy(4f, 0f), "CancelButton".Translate()))
            Close();
    }
}
