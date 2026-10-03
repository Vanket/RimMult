using System;
using System.Collections.Generic;
using Verse;

namespace RimMult.Sync;

/// <summary>Per-save multiplayer data: which shared world this save belongs to, and (for hosts) the world's state.</summary>
public sealed class RimMultGameComp : GameComponent
{
    /// <summary>The shared world this colony lives in; null for an ordinary single-player save.</summary>
    public string? WorldId;

    /// <summary>
    /// Base64 <see cref="Shared.World.WorldState"/> of the world this save hosted: other players' colonies and the
    /// world clock, so hosting again from this save restores them.
    /// </summary>
    public string? HostedWorld;

    /// <summary>Parcels packed (pods landed, caravan unloaded) but not yet handed to the server.</summary>
    public List<ParcelRecord> Outbox = new();

    /// <summary>Parcels received but not yet dropped on a map.</summary>
    public List<ParcelRecord> Inbox = new();

    /// <summary>Ids of parcels already received, to ignore the server's repeats.</summary>
    public List<long> ReceivedParcels = new();

    /// <summary>Other players' raids on this colony that are still going on.</summary>
    public List<RaidRecord> Raids = new();

    public RimMultGameComp(Game game)
    {
    }

    public static RimMultGameComp? Instance => Current.Game?.GetComponent<RimMultGameComp>();

    public override void ExposeData()
    {
        if (Scribe.mode == LoadSaveMode.Saving && WorldId != null && Multiplayer.HostedWorldState(WorldId) is { } state)
            HostedWorld = Convert.ToBase64String(state.Serialize());

        Scribe_Values.Look(ref WorldId, "worldId");
        Scribe_Values.Look(ref HostedWorld, "hostedWorld");
        Scribe_Collections.Look(ref Outbox, "parcelOutbox", LookMode.Deep);
        Scribe_Collections.Look(ref Inbox, "parcelInbox", LookMode.Deep);
        Scribe_Collections.Look(ref ReceivedParcels, "receivedParcels", LookMode.Value);
        Outbox ??= new List<ParcelRecord>();
        Inbox ??= new List<ParcelRecord>();
        Scribe_Collections.Look(ref Raids, "playerRaids", LookMode.Deep);
        ReceivedParcels ??= new List<long>();
        Raids ??= new List<RaidRecord>();
        if (Scribe.mode == LoadSaveMode.PostLoadInit)
            Raids.RemoveAll(r => r == null);
    }

    public override void GameComponentTick()
    {
        if (Raids.Count > 0 && Find.TickManager.TicksGame % PlayerRaids.CheckIntervalTicks == 0)
            PlayerRaids.CheckRaids(this);
    }

    public override void StartedNewGame() => WorldSync.OnStartedNewGame(this);
}
