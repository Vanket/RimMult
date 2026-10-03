using System;
using System.Collections.Generic;
using System.Linq;
using RimMult.ClientCore;
using RimMult.Shared.World;
using RimWorld;
using UnityEngine;
using Verse;

namespace RimMult.Sync;

/// <summary>
/// Connects the running game to the shared world: enters it when a save of this world is being played,
/// aligns the calendar to the world clock, reports this player's colonies and leaves on return to the menu.
/// </summary>
internal static class WorldSync
{
    private const float ColonyReportInterval = 2f;

    private static bool _entering;
    private static float _lastColonyCheck;
    private static string _lastColoniesKey = "";

    /// <summary>The player started "Create colony" from the multiplayer window and is in the new-game pages.</summary>
    public static bool NewColonyFlowActive { get; set; }

    public static bool InWorld { get; private set; }

    public static void Attach(ClientSession session) => session.ClockReceived += OnClock;

    public static void Reset()
    {
        _entering = false;
        InWorld = false;
        _lastColoniesKey = "";
        NewColonyFlowActive = false;
        TimeSync.End();
        RemoteColonies.Clear();
    }

    /// <summary>Whether the loaded save belongs to the server's world.</summary>
    public static bool CurrentGameIsInWorld(ClientSession session) =>
        Current.ProgramState == ProgramState.Playing
        && session.World != null
        && RimMultGameComp.Instance?.WorldId == session.World.WorldId;

    public static void Update(ClientSession? session)
    {
        if (session == null || session.State != ClientState.Connected)
        {
            if (InWorld || _entering || RemoteColonies.Any)
                Reset();
            return;
        }

        // The new-game pages were closed without starting a game: the flow is over.
        if (NewColonyFlowActive && Current.ProgramState == ProgramState.Entry
            && !Find.WindowStack.Windows.Any(w => w is Page) && !LongEventHandler.AnyEventNowOrWaiting)
        {
            NewColonyFlowActive = false;
        }

        var shouldBeIn = CurrentGameIsInWorld(session);
        if (shouldBeIn && !InWorld && !_entering)
        {
            _entering = true;
            session.EnterWorld(session.World!.WorldId);
        }
        else if (!shouldBeIn && (InWorld || _entering))
        {
            session.LeaveWorld();
            _entering = false;
            InWorld = false;
            _lastColoniesKey = "";
            TimeSync.End();
        }

        if (InWorld)
        {
            TimeSync.Update(session);
            ReportColonies(session);
        }

        // Show other players' colonies while playing this world, and on the planet picked for a new colony.
        var showColonies = shouldBeIn || (NewColonyFlowActive && session.World != null && Current.Game?.World != null);
        RemoteColonies.Reconcile(session, showColonies);
    }

    /// <summary>A new game started from the multiplayer window: tie it to the shared world (or create the world).</summary>
    public static void OnStartedNewGame(RimMultGameComp comp)
    {
        var session = Multiplayer.Session;
        if (!NewColonyFlowActive || session == null || session.State != ClientState.Connected)
            return;
        NewColonyFlowActive = false;

        if (session.World != null)
        {
            comp.WorldId = session.World.WorldId;
            return;
        }

        // Nobody created the world yet: this planet becomes the shared one.
        ShareCurrentWorld(session, comp);
    }

    /// <summary>Offers the loaded game's planet as the server's world (a server without a world yet).</summary>
    public static void ShareCurrentWorld(ClientSession session, RimMultGameComp comp)
    {
        var id = Guid.NewGuid().ToString("N");
        comp.WorldId = id;
        session.CreateWorld(WorldDefinitions.FromCurrentWorld(id), Find.TickManager.TicksAbs);
    }

    private static void OnClock(long worldTick)
    {
        var tickManager = Find.TickManager;
        if (!_entering || tickManager == null)
            return;

        // Move the calendar so this colony continues at the shared date. Colony time itself (TicksGame) is
        // untouched: a colony that was offline was simply frozen.
        var delta = worldTick - tickManager.TicksAbs;
        if (delta != 0)
        {
            tickManager.gameStartAbsTick += (int)delta;
            Log.Message($"[RimMult] Calendar moved by {delta} ticks to the shared world date.");
        }

        _entering = false;
        InWorld = true;
        TimeSync.Begin();
    }

    private static void ReportColonies(ClientSession session)
    {
        var now = Time.realtimeSinceStartup;
        if (now - _lastColonyCheck < ColonyReportInterval)
            return;
        _lastColonyCheck = now;

        var colonies = new List<ColonyInfo>();
        foreach (var settlement in Find.WorldObjects.Settlements)
        {
            if (settlement.Faction == Faction.OfPlayer)
                colonies.Add(new ColonyInfo { Name = settlement.Name ?? settlement.Label, Tile = settlement.Tile.ToString() });
        }

        var key = string.Join("\n", colonies.Select(c => c.Tile + "|" + c.Name));
        if (key == _lastColoniesKey)
            return;
        _lastColoniesKey = key;
        session.SendColonies(colonies);
    }
}
