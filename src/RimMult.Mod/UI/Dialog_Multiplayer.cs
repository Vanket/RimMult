using System.Collections.Generic;
using System.Linq;
using RimMult.ClientCore;
using RimMult.Coop;
using RimMult.Shared.Coop;
using RimMult.Shared.Mods;
using RimMult.Shared.Packets;
using RimMult.Steam;
using RimMult.Sync;
using RimWorld;
using UnityEngine;
using Verse;

namespace RimMult.UI;

/// <summary>
/// The multiplayer window: join/host when not connected, lobby (players + chat) when connected,
/// and the reason (with the mod list differences) when a connection ended.
/// </summary>
internal sealed class Dialog_Multiplayer : Window
{
    private const float RowHeight = 30f;
    private const float Gap = 10f;

    private readonly ChatPanel _chat = new("RimMultLobbyChatInput");
    private string _password = "";
    private string _hostPassword = "";
    private string _hostPortBuffer = "";
    private string? _error;
    private Vector2 _friendsScroll;
    private Vector2 _playersScroll;
    private Vector2 _diffScroll;

    public Dialog_Multiplayer()
    {
        doCloseX = true;
        forcePause = false;
        absorbInputAroundWindow = true;
        closeOnClickedOutside = false;
        closeOnAccept = false;
        onlyOneOfTypeAllowed = true;
        optionalTitle = "RimMult.Multiplayer".Translate() + " · RimMult " + RimMultMod.Build;
    }

    public override Vector2 InitialSize => new(860f, 700f);

    private static RimMultSettings Settings => RimMultMod.Instance.Settings;

    public override void PreClose()
    {
        base.PreClose();
        RimMultMod.Instance.WriteSettings();
    }

    public override void DoWindowContents(Rect inRect)
    {
        Text.Font = GameFont.Small;
        var session = Multiplayer.Session;

        if (!SteamIntegration.Available)
        {
            Widgets.Label(inRect, "RimMult.SteamRequired".Translate());
            return;
        }

        if (session != null && session.State != ClientState.Disconnected)
        {
            DrawLobby(inRect, session);
            return;
        }

        var setupRect = inRect;
        if (session != null)
        {
            // The last connection ended: say why, on top of the join/host controls.
            var endedHeight = session.ModDiff is { IsEmpty: false } ? 270f : session.KickReason == KickReason.WrongPassword ? 92f : 60f;
            DrawEnded(new Rect(inRect.x, inRect.y, inRect.width, endedHeight), session);
            setupRect.yMin += endedHeight + Gap;
        }
        DrawSetup(setupRect);
    }

    private void DrawSetup(Rect rect)
    {
        if (_error != null)
        {
            var errorRect = new Rect(rect.x, rect.y, rect.width, RowHeight);
            GUI.color = ColorLibrary.RedReadable;
            Widgets.Label(errorRect, _error);
            GUI.color = Color.white;
            rect.yMin += RowHeight;
        }

        var left = rect.LeftHalf().ContractedBy(4f);
        var right = rect.RightHalf().ContractedBy(4f);
        DrawJoin(left);
        DrawHost(right);
    }

    private void DrawJoin(Rect rect)
    {
        Widgets.DrawMenuSection(rect);
        var list = new Listing_Standard();
        list.Begin(rect.ContractedBy(10f));

        Text.Font = GameFont.Medium;
        list.Label("RimMult.Join".Translate());
        Text.Font = GameFont.Small;

        // The password applies to both ways of joining below, so it comes first.
        _password = list.TextEntryLabeled("RimMult.ServerPassword".Translate(), _password);
        list.Gap(6f);

        list.Label("RimMult.FriendsHosting".Translate());
        var friends = SteamIntegration.FriendHosts();
        var friendsRect = list.GetRect(120f);
        Widgets.DrawMenuSection(friendsRect);
        var inner = friendsRect.ContractedBy(4f);
        if (friends.Count == 0)
        {
            GUI.color = Color.gray;
            Widgets.Label(inner, "RimMult.NoFriendsHosting".Translate());
            GUI.color = Color.white;
        }
        else
        {
            var view = new Rect(0f, 0f, inner.width - 16f, friends.Count * RowHeight);
            Widgets.BeginScrollView(inner, ref _friendsScroll, view);
            for (var i = 0; i < friends.Count; i++)
            {
                var row = new Rect(0f, i * RowHeight, view.width, RowHeight - 2f);
                Widgets.Label(row.LeftPart(0.46f), friends[i].Name);
                var check = new Rect(row.x + row.width * 0.48f, row.y, row.width * 0.24f, row.height);
                if (Widgets.ButtonText(check, "RimMult.ModCheckButton".Translate()))
                    Multiplayer.CheckModsSteam(friends[i].SteamId);
                TooltipHandler.TipRegion(check, "RimMult.ModCheckTip".Translate());
                if (Widgets.ButtonText(row.RightPart(0.26f), "RimMult.JoinButton".Translate()))
                    Multiplayer.JoinSteam(friends[i].SteamId, _password);
            }
            Widgets.EndScrollView();
        }

        list.Gap();
        Settings.LastServerAddress = list.TextEntryLabeled("RimMult.Address".Translate(), Settings.LastServerAddress);
        if (list.ButtonText("RimMult.JoinByAddress".Translate()))
        {
            _error = Multiplayer.JoinAddress(Settings.LastServerAddress, _password, out var error) ? null : error;
        }
        if (list.ButtonText("RimMult.ModCheckByAddress".Translate()))
        {
            _error = Multiplayer.CheckModsAddress(Settings.LastServerAddress, out var error) ? null : error;
        }

        list.Gap();
        GUI.color = Color.gray;
        list.Label("RimMult.JoinHint".Translate());
        GUI.color = Color.white;
        list.End();
    }

    private void DrawHost(Rect rect)
    {
        Widgets.DrawMenuSection(rect);
        var list = new Listing_Standard();
        list.Begin(rect.ContractedBy(10f));

        Text.Font = GameFont.Medium;
        list.Label("RimMult.Host".Translate());
        Text.Font = GameFont.Small;

        Settings.HostServerName = list.TextEntryLabeled("RimMult.ServerName".Translate(), Settings.HostServerName);
        Settings.HostMaxPlayers = Mathf.RoundToInt(list.SliderLabeled(
            "RimMult.MaxPlayers".Translate(Settings.HostMaxPlayers), Settings.HostMaxPlayers, 2f, 10f));
        _hostPassword = list.TextEntryLabeled("RimMult.HostPassword".Translate(), _hostPassword);

        DrawModeChoice(list);

        list.CheckboxLabeled("RimMult.OpenPort".Translate(), ref Settings.HostOpenPort, "RimMult.OpenPortTip".Translate());
        if (Settings.HostOpenPort)
            list.TextFieldNumericLabeled("RimMult.Port".Translate(), ref Settings.HostPort, ref _hostPortBuffer, 1, 65535);

        list.Gap();
        var options = new HostOptions
        {
            ServerName = Settings.HostServerName,
            MaxPlayers = Settings.HostMaxPlayers,
            Password = _hostPassword,
            OpenPort = Settings.HostOpenPort,
            Port = Settings.HostPort,
            Mode = Settings.HostMode,
            AllowPvp = Settings.HostAllowPvp,
        };
        if (list.ButtonText("RimMult.HostButton".Translate()))
            _error = Multiplayer.Host(options, out var error) ? null : error;

        // From the main menu: pick a save, and the game is hosted as soon as it has loaded.
        if (Current.ProgramState == ProgramState.Entry && list.ButtonText("RimMult.LoadAndHost".Translate()))
        {
            Multiplayer.HostAfterLoad(options);
            Close();
            Find.WindowStack.Add(new Dialog_SaveFileList_Load());
        }

        list.Gap();
        GUI.color = Color.gray;
        list.Label("RimMult.HostHint".Translate());
        GUI.color = Color.white;
        list.End();
    }

    /// <summary>How the friends play: each their own colony, or all together in the host's.</summary>
    private static void DrawModeChoice(Listing_Standard list)
    {
        list.Gap(4f);
        list.Label("RimMult.GameMode".Translate());
        DrawMode(list, GameMode.SeparateColonies, "RimMult.ModeSeparate", "RimMult.ModeSeparateDesc");
        DrawMode(list, GameMode.Coop, "RimMult.ModeCoop", "RimMult.ModeCoopDesc");
        if (Settings.HostMode == GameMode.SeparateColonies)
            list.CheckboxLabeled("RimMult.AllowPvp".Translate(), ref Settings.HostAllowPvp, "RimMult.AllowPvpTip".Translate());
        list.Gap(4f);
    }

    private static void DrawMode(Listing_Standard list, GameMode mode, string labelKey, string descriptionKey)
    {
        if (list.RadioButton(labelKey.Translate(), Settings.HostMode == mode))
            Settings.HostMode = mode;
        var font = Text.Font;
        Text.Font = GameFont.Tiny;
        GUI.color = Color.gray;
        var description = descriptionKey.Translate();
        var rect = list.GetRect(Text.CalcHeight(description, list.ColumnWidth - 24f));
        Widgets.Label(new Rect(rect.x + 24f, rect.y, rect.width - 24f, rect.height), description);
        GUI.color = Color.white;
        Text.Font = font;
        list.Gap(2f);
    }

    private void DrawLobby(Rect rect, ClientSession session)
    {
        var header = new Rect(rect.x, rect.y, rect.width, RowHeight);
        Text.Font = GameFont.Medium;
        Widgets.Label(header, session.State == ClientState.Connected
            ? session.ServerName
            : (string)"RimMult.StatusConnecting".Translate());
        Text.Font = GameFont.Small;
        if (session.State == ClientState.Connected
            && Widgets.ButtonText(new Rect(header.xMax - 200f, header.y, 200f, RowHeight), "RimMult.ChronicleButton".Translate()))
        {
            Window_Chronicle.Open();
        }
        if (session.State == ClientState.Connected && session.Mode != Shared.Coop.GameMode.Coop
            && Widgets.ButtonText(new Rect(header.xMax - 410f, header.y, 200f, RowHeight), "RimMult.MarketButton".Translate()))
        {
            Window_Market.Open();
        }

        var bottom = new Rect(rect.x, rect.yMax - RowHeight - 4f, rect.width, RowHeight + 4f);
        var worldRect = new Rect(rect.x, header.yMax + Gap, rect.width, 76f);
        if (session.State == ClientState.Connected)
            DrawWorld(worldRect, session);
        var body = new Rect(rect.x, worldRect.yMax + Gap, rect.width, bottom.y - worldRect.yMax - 2 * Gap);

        var playersRect = body.LeftPartPixels(260f);
        var chatRect = new Rect(playersRect.xMax + Gap, body.y, body.width - playersRect.width - Gap, body.height);
        DrawPlayers(playersRect, session);
        if (session.State == ClientState.Connected)
            _chat.Draw(chatRect, session);
        else
            Widgets.Label(chatRect, "RimMult.StatusHandshaking".Translate());

        if (Multiplayer.IsHosting)
        {
            var info = Multiplayer.HostedPort is { } port
                ? "RimMult.HostingInfoPort".Translate(port)
                : "RimMult.HostingInfo".Translate();
            GUI.color = Color.gray;
            Widgets.Label(bottom.LeftPart(0.45f), info);
            GUI.color = Color.white;
        }

        var reportRect = new Rect(bottom.xMax - 410f, bottom.y, 200f, bottom.height);
        if (Widgets.ButtonText(reportRect, "RimMult.Report".Translate()))
            Diagnostics.WriteReport();
        TooltipHandler.TipRegion(reportRect, "RimMult.ReportHint".Translate());

        var leaveLabel = Multiplayer.IsHosting ? "RimMult.StopHosting".Translate() : "RimMult.Disconnect".Translate();
        if (Widgets.ButtonText(bottom.RightPartPixels(200f), leaveLabel))
            Multiplayer.Stop();
    }

    /// <summary>State of the shared world and what this player can do about it right now.</summary>
    private void DrawWorld(Rect rect, ClientSession session)
    {
        Widgets.DrawMenuSection(rect);
        var inner = rect.ContractedBy(8f);
        var textRect = new Rect(inner.x, inner.y, inner.width - 230f, inner.height);
        var buttons = new Rect(inner.xMax - 220f, inner.y, 220f, inner.height);
        var world = session.World;
        var comp = RimMultGameComp.Instance;
        var playing = Current.ProgramState == ProgramState.Playing;

        var firstButton = new Rect(buttons.x, buttons.y, buttons.width, RowHeight);
        if (CoopGuest.IsGuest(session))
        {
            DrawCoopGuest(textRect, firstButton, session);
            return;
        }

        var waitingForHost = world == null && session.HostCreatesWorld && !session.IsHost;

        string status;
        if (world != null)
            status = "RimMult.WorldInfo".Translate(world.SeedString, session.Colonies.Count);
        else if (WorldSync.WorldCreatePending)
            status = "RimMult.WorldCreating".Translate();
        else if (waitingForHost)
            status = "RimMult.WaitingForHostWorld".Translate();
        else
            status = "RimMult.WorldNone".Translate();

        if (WorldSync.InWorld)
        {
            status += "\n" + "RimMult.InWorld".Translate();
        }
        else if (waitingForHost || WorldSync.WorldCreatePending)
        {
            // Nothing to do but wait.
        }
        else if (playing && world != null && comp != null && comp.WorldId != world.WorldId)
        {
            // A save of the same planet (e.g. the one the world was made from) can simply be attached.
            if (WorldSync.SaveMatchesWorld(world))
            {
                status += "\n" + "RimMult.SameWorldSave".Translate();
                if (Widgets.ButtonText(firstButton, "RimMult.AttachSave".Translate()))
                    comp.WorldId = world.WorldId;
            }
            else
            {
                status += "\n" + "RimMult.OtherSave".Translate();
            }
        }
        else if (playing && world == null && comp != null)
        {
            status += "\n" + "RimMult.CanShareWorld".Translate();
            if (Widgets.ButtonText(firstButton, "RimMult.ShareWorld".Translate()))
                WorldSync.ShareCurrentWorld(session, comp);
        }
        else if (!playing)
        {
            if (Widgets.ButtonText(firstButton, "RimMult.CreateColony".Translate()))
            {
                WorldSync.NewColonyFlowActive = true;
                Close();
                Find.WindowStack.Add(new Page_SelectScenario());
            }
            if (world != null
                && Widgets.ButtonText(new Rect(buttons.x, buttons.y + RowHeight + 4f, buttons.width, RowHeight), "RimMult.LoadColony".Translate()))
            {
                Close();
                Find.WindowStack.Add(new Dialog_SaveFileList_Load());
            }
        }

        if (session.Mode == GameMode.Coop)
            status = "RimMult.ModeCoopHostInfo".Translate() + "\n" + status;
        Widgets.Label(textRect, status);
    }

    /// <summary>Co-op guest: no world of their own, just the way into the host's colony.</summary>
    private void DrawCoopGuest(Rect textRect, Rect button, ClientSession session)
    {
        var host = session.Players.FirstOrDefault(p => p.IsHost);
        string status = "RimMult.ModeCoopGuestInfo".Translate(host?.Name ?? "?");
        switch (CoopGuest.State)
        {
            case CoopGuest.Phase.None:
                status += "\n" + "RimMult.CoopJoinHint".Translate();
                if (Widgets.ButtonText(button, "RimMult.CoopJoin".Translate()))
                    CoopGuest.RequestJoin(session);
                break;
            case CoopGuest.Phase.Requested:
                status += "\n" + "RimMult.CoopRequested".Translate();
                if (Widgets.ButtonText(button, "RimMult.CoopCancel".Translate()))
                    CoopGuest.CancelJoin(session);
                break;
            case CoopGuest.Phase.Loading:
                status += "\n" + "RimMult.CoopLoading".Translate();
                break;
            case CoopGuest.Phase.Active:
                status += "\n" + "RimMult.CoopPlaying".Translate();
                if (Widgets.ButtonText(button, "RimMult.CoopReload".Translate()))
                    CoopGuest.Reload();
                TooltipHandler.TipRegion(button, "RimMult.CoopReloadHint".Translate());
                break;
        }
        Widgets.Label(textRect, status);
    }

    private void DrawPlayers(Rect rect, ClientSession session)
    {
        Widgets.DrawMenuSection(rect);
        var inner = rect.ContractedBy(6f);

        // Each player, with their colonies underneath; colonies of players who are offline at the end.
        var rows = new List<string>();
        var rowPlayers = new Dictionary<int, PlayerInfo>();
        var online = new HashSet<ulong>();
        foreach (var player in session.Players)
        {
            rowPlayers[rows.Count] = player;
            var label = PlayerPalette.Colorize("■ " + player.Name, player.ColorIndex);
            if (player.IsHost)
                label += " " + "RimMult.HostMark".Translate();
            label += player.InWorld ? " " + "RimMult.PlayingMark".Translate() : " " + "RimMult.LobbyMark".Translate();
            if (player.Id == session.PlayerId)
                label = $"<b>{label}</b>";
            else if (DiplomacyUi.Tag(session.RelationWith(Multiplayer.OwnerKey(player))) is { Length: > 0 } tag)
                label += " " + tag;
            rows.Add(label);

            var key = player.SteamId != 0 ? player.SteamId : (ulong)player.Id;
            online.Add(key);
            rows.AddRange(session.Colonies.Where(c => c.OwnerSteamId == key).Select(c => "    · " + c.Name));
        }
        var offline = session.Colonies.Where(c => !online.Contains(c.OwnerSteamId)).ToList();
        if (offline.Count > 0)
        {
            rows.Add("<color=#999999>" + "RimMult.OfflineColonies".Translate() + "</color>");
            rows.AddRange(offline.Select(c => $"    {PlayerPalette.Colorize("■", c.ColorIndex)} <color=#999999>{c.Name} ({c.OwnerName})</color>"));
        }

        var view = new Rect(0f, 0f, inner.width - 16f, Mathf.Max(rows.Count * 24f, inner.height));
        Widgets.BeginScrollView(inner, ref _playersScroll, view);
        for (var i = 0; i < rows.Count; i++)
        {
            var row = new Rect(0f, i * 24f, view.width, 24f);
            // The host can kick or ban anyone else (the same as "/kick" and "/ban" in chat).
            if (session.IsHost && rowPlayers.TryGetValue(i, out var other) && other.Id != session.PlayerId)
            {
                var menu = new Rect(row.xMax - 24f, row.y, 24f, 24f);
                if (Widgets.ButtonText(menu, "⋮"))
                    Find.WindowStack.Add(new FloatMenu(HostOptions(session, other)));
                TooltipHandler.TipRegion(menu, "RimMult.PlayerActions".Translate(other.Name));
                row.width -= 26f;
            }
            Widgets.Label(row, rows[i]);
        }
        Widgets.EndScrollView();
    }

    /// <summary>What the host can do to another player: kick, or ban (by SteamID, so they stay out).</summary>
    private static List<FloatMenuOption> HostOptions(ClientSession session, PlayerInfo player)
    {
        var options = new List<FloatMenuOption>
        {
            new("RimMult.Kick".Translate(player.Name), () => Find.WindowStack.Add(Dialog_MessageBox.CreateConfirmation(
                "RimMult.KickConfirm".Translate(player.Name), () => session.SendChat($"/kick #{player.Id}"), destructive: true))),
        };
        options.Add(player.SteamId != 0
            ? new FloatMenuOption("RimMult.Ban".Translate(player.Name), () => Find.WindowStack.Add(Dialog_MessageBox.CreateConfirmation(
                "RimMult.BanConfirm".Translate(player.Name), () => session.SendChat($"/ban #{player.Id}"), destructive: true)))
            : new FloatMenuOption("RimMult.Ban".Translate(player.Name) + " (" + "RimMult.BanNoSteam".Translate() + ")", null));
        return options;
    }

    private void DrawEnded(Rect rect, ClientSession session)
    {
        Widgets.DrawMenuSection(rect);
        var inner = rect.ContractedBy(8f);

        var canRetry = Multiplayer.CanRetry;
        var headline = new Rect(inner.x, inner.y, inner.width - (canRetry ? 320f : 110f), RowHeight);
        var (text, good) = Headline(session);
        GUI.color = good ? Color.green : ColorLibrary.RedReadable;
        Widgets.Label(headline, text);
        GUI.color = Color.white;
        if (Widgets.ButtonText(new Rect(inner.xMax - 100f, inner.y, 100f, RowHeight - 4f), "RimMult.Dismiss".Translate()))
        {
            Multiplayer.Stop();
            return;
        }
        if (canRetry && Widgets.ButtonText(new Rect(inner.xMax - 310f, inner.y, 200f, RowHeight - 4f),
                (session.IsModCheck ? "RimMult.JoinButton" : "RimMult.Retry").Translate()))
        {
            Multiplayer.Retry(_password);
            return;
        }

        if (session.KickReason == KickReason.WrongPassword)
        {
            var row = new Rect(inner.x, inner.y + RowHeight + 4f, inner.width, RowHeight);
            _password = Widgets.TextEntryLabeled(row.LeftPart(0.6f), "RimMult.ServerPassword".Translate(), _password);
            GUI.color = Color.gray;
            Widgets.Label(row.RightPart(0.38f), "RimMult.WrongPasswordHint".Translate());
            GUI.color = Color.white;
            return;
        }

        if (session.ModDiff is { IsEmpty: false } diff)
        {
            if (session.ServerMods is { } serverMods)
                DrawModFixes(new Rect(inner.x, inner.y + RowHeight + 4f, inner.width, RowHeight), serverMods, diff);
            DrawModDiff(new Rect(inner.x, inner.y + 2 * RowHeight + 8f, inner.width, inner.height - 2 * RowHeight - 8f), diff);
        }
    }

    /// <summary>What the ended session says: why the connection closed, or what a mod check found.</summary>
    private static (string Text, bool Good) Headline(ClientSession session)
    {
        if (!session.IsModCheck)
            return ("RimMult.ConnectionEnded".Translate(EndReason(session)), false);
        if (session.ServerProtocol == 0)
            return ("RimMult.ModCheckFailed".Translate(EndReason(session)), false);
        if (session.ServerMods == null)
            return ("RimMult.ModCheckNoList".Translate(), false);
        if (session.ServerProtocol != Shared.ProtocolInfo.Version)
            return ("RimMult.ModCheckProtocol".Translate(Shared.ProtocolInfo.Version, session.ServerProtocol), false);
        return session.ModDiff is { IsEmpty: false }
            ? ("RimMult.ModCheckDiffers".Translate(), false)
            : ("RimMult.ModCheckSame".Translate(), true);
    }

    /// <summary>The server's kick reasons come in English; show known ones in the player's language.</summary>
    private static string EndReason(ClientSession session)
    {
        var detail = session.DisconnectReason ?? "";
        if (session.KickReason is not { } reason || reason == KickReason.Unspecified)
            return detail;
        var key = "RimMult.Kick" + reason;
        return key.CanTranslate() ? key.Translate(detail).ToString() : detail;
    }

    /// <summary>"Make everything like the host's", and which RimMult builds meet here.</summary>
    private static void DrawModFixes(Rect rect, IReadOnlyList<ModEntry> serverMods, ModListDiff diff)
    {
        var button = new Rect(rect.x, rect.y, 300f, rect.height);
        if (Widgets.ButtonText(button, "RimMult.ModsFixAll".Translate()))
            ModListSync.ConfirmFixAll(serverMods, diff);
        TooltipHandler.TipRegion(button, "RimMult.ModsFixAllTip".Translate());

        if (ModListSync.HostBuild(serverMods) is { } hostBuild && hostBuild != RimMultMod.Build)
        {
            GUI.color = ColorLibrary.RedReadable;
            Widgets.Label(new Rect(button.xMax + 12f, rect.y + 4f, rect.width - button.width - 12f, rect.height), "RimMult.ModsBuilds".Translate(RimMultMod.Build, hostBuild));
            GUI.color = Color.white;
        }
    }

    private void DrawModDiff(Rect rect, ModListDiff diff)
    {
        var rows = new List<(string Text, ulong WorkshopId)>();
        foreach (var mod in diff.Missing)
            rows.Add(("RimMult.ModMissing".Translate(mod.Name, mod.PackageId), mod.WorkshopId));
        foreach (var mod in diff.DifferentVersion)
            rows.Add(("RimMult.ModDifferent".Translate(mod.Name, mod.PackageId), mod.WorkshopId));
        foreach (var mod in diff.Extra)
            rows.Add(("RimMult.ModExtra".Translate(mod.Name, mod.PackageId), 0UL));
        if (diff.OrderDiffers)
            rows.Add(("RimMult.ModOrder".Translate(), 0UL));
        if (rows.Count == 0)
            rows.Add(("RimMult.ModSettingsDiffer".Translate(), 0UL));
        else if (diff.Missing.Count > 0 || diff.Extra.Count > 0)
            rows.Add(("<color=#999999>" + "RimMult.ClientOnlyDiffHint".Translate() + "</color>", 0UL));

        var view = new Rect(0f, 0f, rect.width - 16f, rows.Count * 26f);
        Widgets.BeginScrollView(rect, ref _diffScroll, view);
        for (var i = 0; i < rows.Count; i++)
        {
            var row = new Rect(0f, i * 26f, view.width, 26f);
            var (text, workshopId) = rows[i];
            Widgets.Label(workshopId != 0 ? row.LeftPart(0.78f) : row, text);
            if (workshopId != 0 && Widgets.ButtonText(row.RightPart(0.2f), "RimMult.OpenWorkshop".Translate()))
                SteamIntegration.OpenWorkshopPage(workshopId);
        }
        Widgets.EndScrollView();
    }
}
