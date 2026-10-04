using System.Collections.Generic;
using System.Linq;
using RimMult.ClientCore;
using RimMult.Shared.Packets;
using RimMult.Shared.World;
using RimWorld;
using UnityEngine;
using Verse;

namespace RimMult.Sync;

/// <summary>
/// Diplomacy with other players: the "Diplomacy" button on their colonies, how relations are shown, and what this
/// player is told when something happens (a war declared on them, a peace offer to answer, …).
/// </summary>
[StaticConstructorOnStartup]
internal static class DiplomacyUi
{
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

        var online = session.Players.Any(p => Multiplayer.OwnerKey(p) == owner);
        FloatMenuOption Proposal(string label, DiplomacyAction action) => online
            ? new FloatMenuOption(label, () => Send(owner, action))
            : new FloatMenuOption(label + " (" + "RimMult.DiplomacyOffline".Translate(name) + ")", null);

        switch (session.RelationWith(owner))
        {
            case PlayerRelation.Neutral:
                options.Add(session.AllowPvp
                    ? new FloatMenuOption("RimMult.DeclareWar".Translate(), () => Confirm("RimMult.DeclareWarConfirm".Translate(name), () => Send(owner, DiplomacyAction.DeclareWar)))
                    : new FloatMenuOption("RimMult.DeclareWar".Translate() + " (" + "RimMult.RaidPvpOff".Translate() + ")", null));
                options.Add(Proposal("RimMult.ProposeAlliance".Translate(), DiplomacyAction.ProposeAlliance));
                break;
            case PlayerRelation.Hostile:
                options.Add(Proposal("RimMult.ProposePeace".Translate(), DiplomacyAction.ProposePeace));
                break;
            case PlayerRelation.Allied:
                options.Add(new FloatMenuOption("RimMult.BreakAlliance".Translate(), () => Confirm("RimMult.BreakAllianceConfirm".Translate(name), () => Send(owner, DiplomacyAction.BreakAlliance))));
                break;
        }
        return options;
    }

    private static void Send(ulong owner, DiplomacyAction action)
    {
        Multiplayer.Session?.SendDiplomacy(owner, action);
        if (action is DiplomacyAction.ProposePeace or DiplomacyAction.ProposeAlliance)
            Messages.Message("RimMult.ProposalSent".Translate(), MessageTypeDefOf.NeutralEvent, historical: false);
    }

    private static void Confirm(string text, System.Action action) =>
        Find.WindowStack.Add(Dialog_MessageBox.CreateConfirmation(text, action, destructive: true));

    private static void OnNotice(DiplomacyNotice notice)
    {
        var session = Multiplayer.Session;
        if (session == null || Find.WindowStack == null)
            return;
        var me = session.MyOwnerKey;
        var toMe = notice.To == me;
        var byMe = notice.From == me;

        switch (notice.Event)
        {
            case DiplomacyEvent.PeaceProposed or DiplomacyEvent.AllianceProposed when toMe:
                var peace = notice.Event == DiplomacyEvent.PeaceProposed;
                Find.WindowStack.Add(new Dialog_MessageBox(
                    (peace ? "RimMult.PeaceProposedText" : "RimMult.AllianceProposedText").Translate(notice.FromName),
                    "RimMult.DiplomacyAccept".Translate(),
                    () => session.SendDiplomacy(notice.From, DiplomacyAction.Accept),
                    "RimMult.DiplomacyDecline".Translate(),
                    () => session.SendDiplomacy(notice.From, DiplomacyAction.Decline)));
                break;
            case DiplomacyEvent.ProposalDeclined when toMe:
                Messages.Message("RimMult.ProposalDeclined".Translate(notice.FromName), MessageTypeDefOf.NegativeEvent, historical: false);
                break;
            case DiplomacyEvent.WarDeclared when toMe:
                Letter("RimMult.WarOnYouLabel".Translate(notice.FromName), "RimMult.WarOnYouText".Translate(notice.FromName), LetterDefOf.ThreatBig);
                break;
            case DiplomacyEvent.PeaceMade or DiplomacyEvent.AllianceMade or DiplomacyEvent.AllianceBroken when toMe || byMe:
                var other = toMe ? notice.FromName : notice.ToName;
                var key = "RimMult.Diplomacy" + notice.Event;
                Letter((key + "Label").Translate(other), (key + "Text").Translate(other),
                    notice.Event == DiplomacyEvent.AllianceBroken ? LetterDefOf.NegativeEvent : LetterDefOf.PositiveEvent);
                break;
            case DiplomacyEvent.WarDeclared or DiplomacyEvent.PeaceMade or DiplomacyEvent.AllianceMade or DiplomacyEvent.AllianceBroken:
                // Between two other players, or our own declaration: a line is enough.
                Messages.Message(("RimMult.DiplomacyNews" + notice.Event).Translate(notice.FromName, notice.ToName), MessageTypeDefOf.NeutralEvent, historical: false);
                break;
        }
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
