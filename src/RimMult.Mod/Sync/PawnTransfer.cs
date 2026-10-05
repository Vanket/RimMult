using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using RimWorld;
using Verse;

namespace RimMult.Sync;

/// <summary>
/// Colonists and colony animals moving to another player's game. A pawn is full of references to its old world
/// (relations, memories of other pawns, beds, areas, policies, ideology, faction). Inside another game those
/// references would point at nothing, or worse at unrelated objects that happen to share an id, so they are cut
/// before departure and re-pointed at the new colony on arrival.
/// </summary>
internal static class PawnTransfer
{
    private static readonly AccessTools.FieldRef<Pawn_PlayerSettings, Dictionary<Map, Area>> AllowedAreas =
        AccessTools.FieldRefAccess<Pawn_PlayerSettings, Dictionary<Map, Area>>("allowedAreas");

    /// <summary>Whether this pawn can move to another player's colony, and if not, why.</summary>
    /// <param name="prisoners">
    /// Prisoners may go too (traded: a ransom, a sale); they arrive free, as the new colony's people. Raids, help and
    /// gifts take only the colony's own.
    /// </param>
    public static bool CanSend(Pawn pawn, out string reason, bool prisoners = false)
    {
        reason = "";
        if (pawn.Dead)
        {
            reason = "RimMult.PawnDead".Translate();
            return false;
        }
        if (VehicleCompat.IsVehicle(pawn))
        {
            reason = "RimMult.PawnVehicle".Translate(pawn.LabelShortCap);
            return false;
        }
        if (prisoners && pawn.IsPrisonerOfColony && pawn.RaceProps.Humanlike)
            return true;
        if (pawn.Faction != Faction.OfPlayer || pawn.IsPrisoner || pawn.IsSlave)
        {
            reason = "RimMult.PawnNotColonist".Translate(pawn.LabelShortCap);
            return false;
        }
        if (pawn.RaceProps.IsMechanoid)
        {
            reason = "RimMult.PawnMechanoid".Translate(pawn.LabelShortCap);
            return false;
        }
        if (pawn.royalty != null && pawn.royalty.AllTitlesForReading.Count > 0)
        {
            reason = "RimMult.PawnTitled".Translate(pawn.LabelShortCap);
            return false;
        }
        if (pawn.mechanitor != null && pawn.mechanitor.OverseenPawns.Count > 0)
        {
            reason = "RimMult.PawnMechanitor".Translate(pawn.LabelShortCap);
            return false;
        }
        return true;
    }

    /// <summary>Cuts ties to the sender's game. Only for pawns that are leaving for good.</summary>
    public static void PrepareToLeave(Pawn pawn)
    {
        pawn.jobs?.StopAll();
        pawn.jobs?.ClearQueuedJobs();
        pawn.mindState?.Reset(clearInspiration: false, clearMentalState: true);
        if (pawn.drafter != null)
            pawn.drafter.Drafted = false;
        if (pawn.IsPrisoner)
            pawn.guest?.SetGuestStatus(null);
        pawn.ownership?.UnclaimAll();
        CutReferences(pawn);
    }

    /// <summary>Makes an arrived pawn a member of this colony and points its settings at this game's objects.</summary>
    public static void WelcomeArrived(Pawn pawn)
    {
        // The faction reference was resolved by id against this game: whatever it found is wrong.
        pawn.SetFactionDirect(null);
        pawn.SetFaction(Faction.OfPlayer);

        // Ownership was resolved by id too (some random bed): drop it.
        pawn.ownership?.UnclaimAll();
        CutReferences(pawn);

        if (ModsConfig.IdeologyActive && pawn.ideo != null && Faction.OfPlayer.ideos?.PrimaryIdeo is { } ideo)
            pawn.ideo.SetIdeo(ideo);
        if (pawn.outfits != null)
            pawn.outfits.CurrentApparelPolicy = Current.Game.outfitDatabase.DefaultOutfit();
        if (pawn.drugs != null)
            pawn.drugs.CurrentPolicy = Current.Game.drugPolicyDatabase.DefaultDrugPolicy();
        if (pawn.foodRestriction != null)
            pawn.foodRestriction.CurrentFoodPolicy = Current.Game.foodRestrictionDatabase.DefaultFoodRestriction();
    }

    /// <summary>References to other pawns and to map objects; used on both ends.</summary>
    private static void CutReferences(Pawn pawn)
    {
        pawn.relations?.ClearAllRelations();

        var memories = pawn.needs?.mood?.thoughts?.memories;
        if (memories != null)
        {
            foreach (var memory in memories.Memories.Where(m => m.otherPawn != null).ToList())
                memories.RemoveMemory(memory);
        }

        if (pawn.playerSettings != null)
        {
            pawn.playerSettings.Master = null;
            AllowedAreas(pawn.playerSettings)?.Clear();
        }
    }
}
