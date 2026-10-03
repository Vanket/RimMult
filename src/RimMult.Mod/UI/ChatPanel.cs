using RimMult.ClientCore;
using UnityEngine;
using Verse;

namespace RimMult.UI;

/// <summary>Chat log with an input line; shared by the lobby dialog and the in-game chat window.</summary>
internal sealed class ChatPanel
{
    private const float InputHeight = 30f;
    private const float SendButtonWidth = 90f;

    private readonly string _controlName;
    private string _input = "";
    private Vector2 _scroll;
    private int _seenLines = -1;

    public ChatPanel(string controlName)
    {
        _controlName = controlName;
    }

    public void FocusInput() => GUI.FocusControl(_controlName);

    public void Draw(Rect rect, ClientSession session)
    {
        var logRect = new Rect(rect.x, rect.y, rect.width, rect.height - InputHeight - 4f);
        var inputRect = new Rect(rect.x, rect.yMax - InputHeight, rect.width - SendButtonWidth - 4f, InputHeight);
        var sendRect = new Rect(inputRect.xMax + 4f, inputRect.y, SendButtonWidth, InputHeight);

        DrawLog(logRect, session);

        // Enter sends. Checked before drawing the field so the window never sees it as "accept".
        var enterPressed = Event.current.type == EventType.KeyDown
                           && Event.current.keyCode is KeyCode.Return or KeyCode.KeypadEnter
                           && GUI.GetNameOfFocusedControl() == _controlName;

        GUI.SetNextControlName(_controlName);
        _input = Widgets.TextField(inputRect, _input, 500);

        if (enterPressed || Widgets.ButtonText(sendRect, "RimMult.Send".Translate()))
        {
            session.SendChat(_input);
            _input = "";
            if (enterPressed)
                Event.current.Use();
            FocusInput();
        }
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

        // Stick to the bottom when new lines arrive.
        if (session.Chat.Count != _seenLines)
        {
            _seenLines = session.Chat.Count;
            _scroll.y = float.MaxValue;
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
        return $"{PlayerPalette.Colorize(line.SenderName, session.ColorOf(line.SenderId))}: {line.Text.Replace("<", "‹")}";
    }
}
