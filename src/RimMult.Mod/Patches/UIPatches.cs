using System.Collections.Generic;
using HarmonyLib;
using RimMult.UI;
using RimWorld;
using Verse;

namespace RimMult.Patches;

/// <summary>
/// Adds "Multiplayer" to the main menu, right after "Load game" (or "New colony" when there are no saves),
/// and to the in-game menu after "Save" (or before "Options"), to host the loaded colony.
/// </summary>
[HarmonyPatch(typeof(OptionListingUtility), nameof(OptionListingUtility.DrawOptionListing))]
internal static class MainMenuButtonPatch
{
    private static void Prefix(List<ListableOption> optList)
    {
        // The menu draws two lists (game options and web links); only the first contains our anchors.
        string label = "RimMult.Multiplayer".Translate();
        if (optList.Exists(o => o.label == label))
            return;

        int index;
        if (Current.ProgramState == ProgramState.Entry)
        {
            index = optList.FindIndex(o => o.label == "LoadGame".Translate());
            if (index < 0)
                index = optList.FindIndex(o => o.label == "NewColony".Translate());
            if (index >= 0)
                index++;
        }
        else if (Current.ProgramState == ProgramState.Playing)
        {
            index = optList.FindIndex(o => o.label == "Save".Translate());
            if (index >= 0)
                index++;
            else
                index = optList.FindIndex(o => o.label == "Options".Translate());
        }
        else
        {
            return;
        }

        if (index >= 0)
            optList.Insert(index, new ListableOption(label, Multiplayer.OpenDialog));
    }
}

[HarmonyPatch(typeof(UIRoot_Entry), nameof(UIRoot_Entry.UIRootOnGUI))]
internal static class EntryOverlayPatch
{
    private static void Prefix() => StatusOverlay.HandleClicks();

    private static void Postfix() => StatusOverlay.OnGUI();
}

[HarmonyPatch(typeof(UIRoot_Play), nameof(UIRoot_Play.UIRootOnGUI))]
internal static class PlayOverlayPatch
{
    private static void Prefix() => StatusOverlay.HandleClicks();

    private static void Postfix() => StatusOverlay.OnGUI();
}
