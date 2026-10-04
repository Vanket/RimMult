using RimMult.Shared.World;
using Verse;

namespace RimMult.UI;

/// <summary>A player's reputation in words, from what the chronicle counted: wars started, treaties broken and kept.</summary>
internal static class Reputation
{
    /// <summary>"reliable", "unreliable", "aggressor" or "nothing notable yet".</summary>
    public static string Label(PlayerStats stats)
    {
        if (stats.TreatiesBroken > stats.TreatiesKept)
            return "RimMult.ReputationUnreliable".Translate();
        if (stats.WarsDeclared >= 3 && stats.WarsDeclared > stats.TreatiesKept)
            return "RimMult.ReputationAggressor".Translate();
        if (stats.TreatiesKept > 0 || stats.TributePaid > 0)
            return "RimMult.ReputationReliable".Translate();
        return "RimMult.ReputationUnknown".Translate();
    }

    /// <summary>The line on a colony's inspect pane.</summary>
    public static string Inspect(PlayerStats stats) =>
        "RimMult.ReputationInspect".Translate(Label(stats), stats.WarsDeclared, stats.TreatiesBroken, stats.TreatiesKept);
}
