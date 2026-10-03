using RimMult.ClientCore;
using RimMult.Coop;
using RimMult.Shared.Time;
using RimMult.Sync;
using UnityEngine;
using Verse;

namespace RimMult.UI;

/// <summary>
/// Small connection indicator at the top of the screen (main menu and in game); click it to open the multiplayer
/// dialog. Also handles the chat hotkey, since it is drawn every GUI pass.
/// </summary>
internal static class StatusOverlay
{
    private const float MinWidth = 300f;
    private const float Height = 24f;

    public static void OnGUI()
    {
        var session = Multiplayer.Session;
        if (session == null)
            return;

        HandleChatKey(session);

        var font = Text.Font;
        var anchor = Text.Anchor;
        Text.Font = GameFont.Small;
        Text.Anchor = TextAnchor.MiddleCenter;

        // Sized to the text, so a long status stays on one line.
        var text = Describe(session);
        var width = Mathf.Max(MinWidth, Text.CalcSize(text).x + 24f);
        var rect = new Rect((Verse.UI.screenWidth - width) / 2f, 2f, width, Height);
        Widgets.DrawBoxSolid(rect, new Color(0f, 0f, 0f, 0.45f));
        Widgets.DrawHighlightIfMouseover(rect);
        Widgets.Label(rect, text);
        Text.Anchor = anchor;
        Text.Font = font;

        if (Widgets.ButtonInvisible(rect))
            Multiplayer.OpenDialog();
    }

    private static string Describe(ClientSession session)
    {
        switch (session.State)
        {
            case ClientState.Connecting:
                return "RimMult.StatusConnecting".Translate();
            case ClientState.Handshaking:
                return "RimMult.StatusHandshaking".Translate();
            case ClientState.Connected:
                break;
            default:
                return "RimMult.StatusDisconnected".Translate();
        }

        if (CoopGuest.State is CoopGuest.Phase.Requested or CoopGuest.Phase.Loading)
            return "RimMult.CoopLoading".Translate();

        // The shared speed only applies to someone playing in the world (or in the host's colony).
        if (!WorldSync.InWorld && !CoopGuest.Active)
            return "RimMult.StatusLobby".Translate(session.Players.Count);

        var speed = session.LastGrant?.Speed ?? GameSpeed.Paused;
        string text = "RimMult.StatusConnected".Translate(session.Players.Count, SpeedLabel(speed));
        if (CoopGuest.Active)
            text += " · " + "RimMult.StatusCoop".Translate();

        if ((TimeSync.MyVote ?? CoopGuest.MyVote) is { } vote && vote != speed)
            text += " " + "RimMult.StatusMyVote".Translate(SpeedLabel(vote));
        if (TimeSync.BlockedByHorizon && speed != GameSpeed.Paused)
        {
            var bottleneck = session.LastGrant?.BottleneckPlayerId ?? -1;
            text += bottleneck >= 0 && bottleneck != session.PlayerId
                ? " " + "RimMult.StatusWaitingFor".Translate(session.NameOf(bottleneck))
                : " " + "RimMult.StatusWaiting".Translate();
        }
        return text;
    }

    public static string SpeedLabel(GameSpeed speed) => ("RimMult.Speed" + speed).Translate();

    private static void HandleChatKey(ClientSession session)
    {
        var key = RimMultDefOf.RimMult_ToggleChat;
        // Never steal the key from a focused text field (chat input, renaming a pawn, ...).
        if (key == null || session.State != ClientState.Connected || GUIUtility.keyboardControl != 0)
            return;
        if (key.KeyDownEvent)
        {
            Window_Chat.Toggle();
            Event.current.Use();
        }
    }
}
