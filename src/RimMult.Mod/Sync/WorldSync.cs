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

    /// <summary>Wealth and colonists for the chronicle's table: slow-changing, a report now and then is plenty.</summary>
    private const float StatsReportInterval = 30f;

    private static float _lastStatsReport = float.NegativeInfinity;

    private static bool _entering;
    private static float _lastColonyCheck;
    private static string _lastColoniesKey = "";

    /// <summary>The player started "Create colony" from the multiplayer window and is in the new-game pages.</summary>
    public static bool NewColonyFlowActive { get; set; }

    public static bool InWorld { get; private set; }

    /// <summary>This client asked the server to make its planet the world and waits for the answer.</summary>
    public static bool WorldCreatePending { get; private set; }

    /// <summary>The world's NPC settlements, received and waiting to be put in place (on the next frame in the world).</summary>
    private static List<NpcSettlement>? _pendingLayout;

    public static void Attach(ClientSession session)
    {
        session.ClockReceived += OnClock;
        // The server has no layout yet: offer this game's (it takes it only from the world's creator / the host).
        session.NpcLayoutWanted += () =>
        {
            if (Current.ProgramState == ProgramState.Playing && Find.World != null && CurrentGameIsInWorld(session))
                session.SendNpcLayout(NpcLayoutSync.Capture());
        };
        session.NpcLayoutReceived += layout => _pendingLayout = layout;
        // Any world answer settles a pending create: accepted, beaten by someone else, or refused.
        session.WorldChanged += () => WorldCreatePending = false;
    }

    public static void Reset()
    {
        _entering = false;
        InWorld = false;
        _lastColoniesKey = "";
        _lastStatsReport = float.NegativeInfinity;
        _pendingLayout = null;
        NewColonyFlowActive = false;
        WorldCreatePending = false;
        TimeSync.End();
        RemoteColonies.Clear();
        SharedSettlements.Reset();
        Tribute.Reset();
        MarketBroker.Reset();
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

        // A co-op guest plays a copy of the host's game (its save says it is in the world): only the host is. The
        // same for a player away leading a live raid in another player's game.
        var coopGuest = Coop.CoopGuest.IsGuest(session) || Coop.CoopGuest.Visiting || PlayerVisit.Travelling;
        var shouldBeIn = !coopGuest && CurrentGameIsInWorld(session);
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

        if (InWorld && _pendingLayout is { } layout)
        {
            _pendingLayout = null;
            try
            {
                NpcLayoutSync.Apply(layout, session.DestroyedSettlements);
            }
            catch (Exception e)
            {
                Log.Error($"[RimMult] Could not match the NPC settlements to the shared world: {e}");
            }
        }

        if (InWorld)
        {
            TimeSync.Update(session);
            ReportColonies(session);
            ReportStats(session);
            Parcels.Update(session);
            Tribute.Update(session);
            MarketBroker.Update(session);
            SharedSettlements.Apply(session);
        }

        // Show other players' colonies while playing this world, and on the planet picked for a new colony.
        var showColonies = shouldBeIn || (!coopGuest && NewColonyFlowActive && session.World != null && Current.Game?.World != null);
        RemoteColonies.Reconcile(session, showColonies);
    }

    /// <summary>Leaves the world right now (before this player loads another player's game for a live raid).</summary>
    public static void LeaveNow(ClientSession session)
    {
        if (InWorld || _entering)
            session.LeaveWorld();
        _entering = false;
        InWorld = false;
        _lastColoniesKey = "";
        TimeSync.End();
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
        // Keep an id the save already has: repeating the request (double click, retry) must not give the save
        // a different id than the one the server accepted first.
        comp.WorldId ??= Guid.NewGuid().ToString("N");
        WorldCreatePending = true;
        session.CreateWorld(WorldDefinitions.FromCurrentWorld(comp.WorldId), Find.TickManager.TicksAbs);
    }

    /// <summary>
    /// Whether the loaded game is on the same planet as the shared world (same seed and parameters), e.g. the save
    /// the world was created from. Such a save can be attached to the world as is.
    /// </summary>
    public static bool SaveMatchesWorld(WorldDefinition world)
    {
        if (Find.World == null)
            return false;
        var mine = WorldDefinitions.FromCurrentWorld(world.WorldId);
        return mine.SeedString == world.SeedString
               && Mathf.Approximately(mine.PlanetCoverage, world.PlanetCoverage)
               && mine.Rainfall == world.Rainfall
               && mine.Temperature == world.Temperature
               && mine.Population == world.Population
               && mine.LandmarkDensity == world.LandmarkDensity
               && Mathf.Approximately(mine.Pollution, world.Pollution);
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

    /// <summary>How the colonies are doing: the home maps' wealth and the free colonists (the server skips repeats).</summary>
    private static void ReportStats(ClientSession session)
    {
        var now = Time.realtimeSinceStartup;
        if (now - _lastStatsReport < StatsReportInterval)
            return;
        _lastStatsReport = now;
        try
        {
            var wealth = Find.Maps.Where(m => m.IsPlayerHome).Sum(m => m.wealthWatcher.WealthTotal);
            var colonists = PawnsFinder.AllMapsCaravansAndTravellingTransporters_Alive_FreeColonists.Count;
            session.ReportColonyStats(wealth, colonists);
        }
        catch (Exception e)
        {
            Log.WarningOnce($"[RimMult] Could not count the colony for the chronicle: {e.Message}", 0x43485253);
        }
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
