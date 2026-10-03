using RimMult.ClientCore;
using UnityEngine;
using Verse;

namespace RimMult.UI;

/// <summary>Small, movable chat window that does not block the game.</summary>
internal sealed class Window_Chat : Window
{
    private readonly ChatPanel _chat = new("RimMultChatWindowInput");

    public Window_Chat()
    {
        doCloseX = true;
        draggable = true;
        resizeable = true;
        preventCameraMotion = false;
        absorbInputAroundWindow = false;
        closeOnClickedOutside = false;
        closeOnAccept = false;
        forcePause = false;
        focusWhenOpened = true;
        onlyOneOfTypeAllowed = true;
        optionalTitle = "RimMult.Chat".Translate();
    }

    public override Vector2 InitialSize => new(440f, 320f);

    public override void PostOpen()
    {
        base.PostOpen();
        _chat.FocusInput();
    }

    protected override void SetInitialSizeAndPosition()
    {
        // Bottom-left, above the main tab bar, out of the way of the colony.
        var size = InitialSize;
        windowRect = new Rect(8f, Verse.UI.screenHeight - size.y - 48f, size.x, size.y);
    }

    public override void DoWindowContents(Rect inRect)
    {
        var session = Multiplayer.Session;
        if (session == null || session.State == ClientState.Disconnected)
        {
            Close();
            return;
        }

        _chat.Draw(inRect, session);
    }

    public static void Toggle()
    {
        if (!Find.WindowStack.TryRemove(typeof(Window_Chat)))
            Find.WindowStack.Add(new Window_Chat());
    }
}
