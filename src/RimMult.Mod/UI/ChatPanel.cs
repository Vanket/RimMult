using RimMult.ClientCore;
using RimMult.Shared.Packets;
using UnityEngine;
using Verse;

namespace RimMult.UI;

/// <summary>
/// Chat log with an input line; shared by the lobby dialog and the in-game chat window. Lines go to everyone or to
/// allies (the button by the input switches), "/w Name" whispers, "/r" answers the last whisper (see <see cref="ChatInput"/>).
/// </summary>
internal sealed class ChatPanel
{
    private const float InputHeight = 30f;
    private const float SendButtonWidth = 90f;
    private const float ScopeButtonWidth = 70f;

    private readonly string _controlName;
    private string _input = "";
    private Vector2 _scroll;
    private int _seenLines = -1;
    private ChatScope _scope = ChatScope.All;

    /// <summary>The other player of the last whisper (for "/r"), or -1.</summary>
    private static int _lastWhisper = -1;

    public ChatPanel(string controlName)
    {
        _controlName = controlName;
    }

    public void FocusInput() => GUI.FocusControl(_controlName);

    public void Draw(Rect rect, ClientSession session)
    {
        var logRect = new Rect(rect.x, rect.y, rect.width, rect.height - InputHeight - 4f);
        var scopeRect = new Rect(rect.x, rect.yMax - InputHeight, ScopeButtonWidth, InputHeight);
        var inputRect = new Rect(scopeRect.xMax + 4f, scopeRect.y, rect.width - ScopeButtonWidth - SendButtonWidth - 8f, InputHeight);
        var sendRect = new Rect(inputRect.xMax + 4f, inputRect.y, SendButtonWidth, InputHeight);

        DrawLog(logRect, session);

        if (Widgets.ButtonText(scopeRect, (_scope == ChatScope.Allies ? "RimMult.ChatScopeAllies" : "RimMult.ChatScopeAll").Translate()))
            _scope = _scope == ChatScope.Allies ? ChatScope.All : ChatScope.Allies;
        TooltipHandler.TipRegion(scopeRect, "RimMult.ChatHelp".Translate());

        // Enter sends. Checked before drawing the field so the window never sees it as "accept".
        var enterPressed = Event.current.type == EventType.KeyDown
                           && Event.current.keyCode is KeyCode.Return or KeyCode.KeypadEnter
                           && GUI.GetNameOfFocusedControl() == _controlName;

        GUI.SetNextControlName(_controlName);
        _input = Widgets.TextField(inputRect, _input, 500);
        TooltipHandler.TipRegion(inputRect, "RimMult.ChatHelp".Translate());

        if (enterPressed || Widgets.ButtonText(sendRect, "RimMult.Send".Translate()))
        {
            Send(session);
            if (enterPressed)
                Event.current.Use();
            FocusInput();
        }
    }

    private void Send(ClientSession session)
    {
        if (_input.Trim().Length == 0)
            return;
        var line = ChatInput.Parse(_input, _scope, session.Players, session.PlayerId, _lastWhisper);
        switch (line.Problem)
        {
            case ChatInputProblem.NoSuchPlayer:
                Messages.Message("RimMult.ChatNoSuchPlayer".Translate(line.Name), RimWorld.MessageTypeDefOf.RejectInput, historical: false);
                return; // keep the line to fix the name
            case ChatInputProblem.NoReply:
                Messages.Message("RimMult.ChatNoReply".Translate(), RimWorld.MessageTypeDefOf.RejectInput, historical: false);
                return;
        }
        if (line.Text.Length > 0)
            session.SendChat(line.Text, line.Scope, line.TargetId);
        _input = "";
    }

    private void DrawLog(Rect rect, ClientSession session)
    {
        Widgets.DrawMenuSection(rect);
        var inner = rect.ContractedBy(6f);
        var width = inner.width - 16f;

        Text.Font = GameFont.Small;
        var height = 0f;
        foreach (var line in session.Chat)
            height += Text.CalcHeight(Format(line, session), width);

        // Stick to the bottom when new lines arrive; remember who whispered last.
        if (session.Chat.Count != _seenLines)
        {
            _seenLines = session.Chat.Count;
            _scroll.y = float.MaxValue;
            if (session.Chat.Count > 0 && session.Chat[session.Chat.Count - 1] is { Scope: ChatScope.Whisper } last)
                _lastWhisper = last.SenderId == session.PlayerId ? last.TargetId : last.SenderId;
        }

        var view = new Rect(0f, 0f, width, Mathf.Max(height, inner.height));
        Widgets.BeginScrollView(inner, ref _scroll, view);
        var y = 0f;
        foreach (var line in session.Chat)
        {
            var text = Format(line, session);
            var lineHeight = Text.CalcHeight(text, width);
            Widgets.Label(new Rect(0f, y, width, lineHeight), text);
            y += lineHeight;
        }
        Widgets.EndScrollView();
    }

    private static string Format(ChatLine line, ClientSession session)
    {
        var text = $"{PlayerPalette.Colorize(line.SenderName, session.ColorOf(line.SenderId))}: {line.Text.Replace("<", "‹")}";
        return line.Scope switch
        {
            ChatScope.Allies => $"<color=#70D070>{"RimMult.ChatTagAllies".Translate()}</color> {text}",
            ChatScope.Whisper when line.SenderId == session.PlayerId =>
                $"<color=#C090F0>{"RimMult.ChatTagWhisperTo".Translate(line.TargetName)}</color> {text}",
            ChatScope.Whisper => $"<color=#C090F0>{"RimMult.ChatTagWhisperFrom".Translate()}</color> {text}",
            _ => text,
        };
    }
}
