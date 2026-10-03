using System;
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
    }

    public override void StartedNewGame() => WorldSync.OnStartedNewGame(this);
}
