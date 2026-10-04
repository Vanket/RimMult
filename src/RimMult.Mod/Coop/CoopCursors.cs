using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using RimMult.ClientCore;
using RimMult.Shared.Coop;
using RimMult.UI;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace RimMult.Coop;

/// <summary>
/// Co-op: everyone sees where the others point and what they have selected, in their color. Guests send their own
/// cursor to the host; the host sends everyone's (its own and the guests') to all guests. Unreliable and small:
/// a lost frame is replaced by the next one.
/// </summary>
internal static class CoopCursors
{
    private const float SendInterval = 1f / 15f;
    private const float KeepAlive = 1f;

    /// <summary>A cursor not heard of for this long is gone (the player tabbed out, left the map, lost connection).</summary>
    private const float Expire = 3f;

    /// <summary>Mouse moves smaller than this (hundredths of a cell) aren't worth a frame.</summary>
    private const int Tolerance = 10;

    private const float LookupRefresh = 0.5f;

    private static readonly Dictionary<int, (CursorMark Mark, float At)> Others = new();
    private static CursorMark? _lastSent;
    private static float _lastSentAt = float.NegativeInfinity;

    private static readonly Dictionary<int, Thing> Lookup = new();
    private static int _lookupMap = -1;
    private static float _lookupAt = float.NegativeInfinity;

    public static void Reset()
    {
        Others.Clear();
        _lastSent = null;
        _lastSentAt = float.NegativeInfinity;
        Lookup.Clear();
        _lookupMap = -1;
    }

    /// <summary>Co-op is on, with someone to share with, and this player wants to see cursors.</summary>
    private static bool Sharing(ClientSession session) =>
        RimMultMod.Instance.Settings.ShowCursors
        && session is { Mode: GameMode.Coop, State: ClientState.Connected }
        && (Multiplayer.IsHosting ? CoopHost.Active && CoopHost.HasGuests : CoopGuest.Active && !CoopGuest.Visiting);

    /// <summary>Called every frame.</summary>
    public static void Update(ClientSession? session)
    {
        if (session == null || !Sharing(session))
        {
            if (Others.Count > 0 || _lastSent != null)
                Reset();
            return;
        }

        var now = Time.realtimeSinceStartup;
        foreach (var gone in Others.Where(p => now - p.Value.At > Expire).Select(p => p.Key).ToList())
            Others.Remove(gone);
        if (now - _lastSentAt < SendInterval)
            return;

        var mine = Mine(session.PlayerId);
        if (Multiplayer.IsHosting)
        {
            // Guests only hear from the host: it passes everyone's cursors on, its own among them.
            var fresh = Others.Values.Any(o => o.At > _lastSentAt);
            if (!fresh && mine.SameAs(_lastSent, Tolerance) && now - _lastSentAt < KeepAlive)
                return;
            var frame = new CursorsFrame();
            frame.Marks.Add(mine);
            frame.Marks.AddRange(Others.Values.Select(o => o.Mark));
            session.SendCoop(CoopChannel.Cursors, frame.Encode());
        }
        else
        {
            if (mine.SameAs(_lastSent, Tolerance) && now - _lastSentAt < KeepAlive)
                return;
            CoopGuest.SendToHost(CoopChannel.Cursors, new CursorsFrame { Marks = { mine } }.Encode());
        }
        _lastSent = mine;
        _lastSentAt = now;
    }

    /// <summary>Where this player points right now.</summary>
    private static CursorMark Mine(int playerId)
    {
        var mark = new CursorMark { PlayerId = playerId };
        var map = Find.CurrentMap;
        if (map == null || WorldRendererUtility.WorldRendered)
            return mark;
        mark.MapId = map.uniqueID;
        var position = Verse.UI.MouseMapPosition();
        mark.X = Mathf.RoundToInt(position.x * 100f);
        mark.Z = Mathf.RoundToInt(position.z * 100f);
        mark.Selected.AddRange(Find.Selector.SelectedObjects.OfType<Thing>()
            .Where(t => t.Spawned && t.Map == map && t.thingIDNumber >= 0)
            .Select(t => t.thingIDNumber)
            .Take(CursorMark.MaxSelected));
        return mark;
    }

    /// <summary>Host: a guest's cursor.</summary>
    public static void FromGuest(int guestId, byte[] data)
    {
        if (Decode(data) is not { Marks.Count: > 0 } frame)
            return;
        var mark = frame.Marks[0];
        mark.PlayerId = guestId;
        Others[guestId] = (mark, Time.realtimeSinceStartup);
    }

    /// <summary>Guest: everyone's cursors, from the host.</summary>
    public static void FromHost(byte[] data)
    {
        if (Decode(data) is not { } frame || Multiplayer.Session is not { } session)
            return;
        var now = Time.realtimeSinceStartup;
        Others.Clear();
        foreach (var mark in frame.Marks.Where(m => m.PlayerId != session.PlayerId))
            Others[mark.PlayerId] = (mark, now);
    }

    private static CursorsFrame? Decode(byte[] data)
    {
        try
        {
            return CursorsFrame.Decode(data);
        }
        catch (System.Exception e)
        {
            Log.WarningOnce($"[RimMult] Co-op: bad cursors: {e.Message}", 0x43555253);
            return null;
        }
    }

    /// <summary>Outlines what the others have selected on this map (world space, every frame).</summary>
    public static void DrawSelections(Map map)
    {
        if (Others.Count == 0 || Multiplayer.Session is not { } session)
            return;
        foreach (var pair in Others)
        {
            var mark = pair.Value.Mark;
            if (mark.MapId != map.uniqueID || mark.Selected.Count == 0)
                continue;
            var color = PlayerPalette.Get(session.ColorOf(pair.Key));
            foreach (var id in mark.Selected)
            {
                if (ThingById(map, id) is not { Spawned: true } thing || thing.Map != map)
                    continue;
                GenDraw.DrawFieldEdges(thing.OccupiedRect().Cells.ToList(), color, null);
            }
        }
    }

    /// <summary>Things by id on a map, rebuilt now and then (selections are drawn every frame).</summary>
    private static Thing? ThingById(Map map, int id)
    {
        var now = Time.realtimeSinceStartup;
        if (_lookupMap != map.uniqueID || now - _lookupAt > LookupRefresh)
        {
            Lookup.Clear();
            foreach (var thing in map.listerThings.AllThings)
            {
                if (thing.thingIDNumber >= 0)
                    Lookup[thing.thingIDNumber] = thing;
            }
            _lookupMap = map.uniqueID;
            _lookupAt = now;
        }
        return Lookup.TryGetValue(id, out var found) ? found : null;
    }

    /// <summary>The others' cursors with their names (screen space, under the windows).</summary>
    public static void OnGUI()
    {
        if (Others.Count == 0 || Event.current.type != EventType.Repaint || Multiplayer.Session is not { } session)
            return;
        var map = Find.CurrentMap;
        if (map == null || WorldRendererUtility.WorldRendered)
            return;

        var font = Text.Font;
        Text.Font = GameFont.Tiny;
        foreach (var pair in Others)
        {
            var mark = pair.Value.Mark;
            if (mark.MapId != map.uniqueID)
                continue;
            var at = Verse.UI.MapToUIPosition(new Vector3(mark.X / 100f, 0f, mark.Z / 100f));
            var color = PlayerPalette.Get(session.ColorOf(pair.Key));
            DrawCross(at, color);

            var name = session.NameOf(pair.Key);
            var size = Text.CalcSize(name);
            var label = new Rect(at.x + 9f, at.y + 5f, size.x + 8f, size.y + 2f);
            Widgets.DrawBoxSolid(label, new Color(0f, 0f, 0f, 0.55f));
            GUI.color = color;
            Widgets.Label(new Rect(label.x + 4f, label.y + 1f, size.x + 2f, size.y), name);
            GUI.color = Color.white;
        }
        Text.Font = font;
    }

    private static void DrawCross(Vector2 at, Color color)
    {
        var shadow = new Color(0f, 0f, 0f, 0.7f);
        Widgets.DrawBoxSolid(new Rect(at.x - 8f, at.y - 2f, 16f, 4f), shadow);
        Widgets.DrawBoxSolid(new Rect(at.x - 2f, at.y - 8f, 4f, 16f), shadow);
        Widgets.DrawBoxSolid(new Rect(at.x - 7f, at.y - 1f, 14f, 2f), color);
        Widgets.DrawBoxSolid(new Rect(at.x - 1f, at.y - 7f, 2f, 14f), color);
    }
}

/// <summary>The others' selections, drawn with the map.</summary>
[HarmonyPatch(typeof(Map), nameof(Map.MapUpdate))]
internal static class CoopCursorsSelectionPatch
{
    private static void Postfix(Map __instance)
    {
        if (__instance == Find.CurrentMap)
            CoopCursors.DrawSelections(__instance);
    }
}

/// <summary>The others' cursors and names, drawn over the map but under the windows.</summary>
[HarmonyPatch(typeof(MapInterface), nameof(MapInterface.MapInterfaceOnGUI_BeforeMainTabs))]
internal static class CoopCursorsGuiPatch
{
    private static void Postfix() => CoopCursors.OnGUI();
}
