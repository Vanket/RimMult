using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using RimMult.Shared;
using RimMult.Steam;
using RimWorld;
using UnityEngine;
using Verse;

namespace RimMult;

public sealed class RimMultMod : Mod
{
    public const string HarmonyId = "vanket.rimmult";

    public RimMultMod(ModContentPack content) : base(content)
    {
        Instance = this;
        Settings = GetSettings<RimMultSettings>();
        var harmony = new Harmony(HarmonyId);
        PatchAll(harmony);
        Patches.CoopOverridePatches.Apply(harmony);

        LongEventHandler.ExecuteWhenFinished(() =>
        {
            var runner = new GameObject("RimMult");
            Object.DontDestroyOnLoad(runner);
            runner.AddComponent<RimMultRunner>();
            SteamIntegration.Init();
        });

        Log.Message($"[RimMult] {content.ModMetaData.ModVersion} loaded, protocol v{ProtocolInfo.Version}");

        // The other multiplayer mod rewrites the same parts of the game (ticks, orders, saving): never both.
        if (ModLister.GetActiveModWithIdentifier("rwmt.Multiplayer", ignorePostfix: true) != null)
        {
            Log.Error("[RimMult] The \"Multiplayer\" mod (rwmt.Multiplayer) is active: it doesn't work together with RimMult. Disable one of them.");
            LongEventHandler.ExecuteWhenFinished(() => Find.WindowStack.Add(new Dialog_MessageBox("RimMult.OtherMultiplayerMod".Translate())));
        }
    }

    /// <summary>Applies the patch classes one by one: one that can't be applied (another mod, a game update) doesn't take the rest down.</summary>
    private static void PatchAll(Harmony harmony)
    {
        foreach (var type in AccessTools.GetTypesFromAssembly(typeof(RimMultMod).Assembly))
        {
            try
            {
                harmony.CreateClassProcessor(type).Patch();
            }
            catch (System.Exception e)
            {
                Log.Error($"[RimMult] Could not apply patch {type.FullName}: {e}");
            }
        }
    }

    public static RimMultMod Instance { get; private set; } = null!;

    public RimMultSettings Settings { get; }

    public override string SettingsCategory() => "RimMult";

    private Vector2 _modsScroll;

    public override void DoSettingsWindowContents(Rect inRect)
    {
        var list = new Listing_Standard();
        list.Begin(inRect);
        list.Label("RimMult.ClientOnlyTitle".Translate());
        GUI.color = Color.gray;
        list.Label("RimMult.ClientOnlyHint".Translate());
        GUI.color = Color.white;

        // Checkbox per mod: client-side mods are not compared with other players.
        var mods = ModList.Running.Where(ModList.CanBeClientOnly).ToList();
        var outer = list.GetRect(inRect.height - list.CurHeight - 100f);
        Widgets.DrawMenuSection(outer);
        var inner = outer.ContractedBy(4f);
        var view = new Rect(0f, 0f, inner.width - 16f, mods.Count * 26f);
        Widgets.BeginScrollView(inner, ref _modsScroll, view);
        for (var i = 0; i < mods.Count; i++)
        {
            var id = mods[i].PackageId.ToLowerInvariant();
            var on = Settings.ClientOnlyMods.Contains(id);
            var was = on;
            Widgets.CheckboxLabeled(new Rect(0f, i * 26f, view.width, 24f), $"{mods[i].Name}  <color=#888888>({mods[i].PackageIdPlayerFacing})</color>", ref on);
            if (on && !was)
                Settings.ClientOnlyMods.Add(id);
            else if (!on && was)
                Settings.ClientOnlyMods.Remove(id);
        }
        Widgets.EndScrollView();

        list.Gap(6f);
        list.CheckboxLabeled("RimMult.SyncModComponents".Translate(), ref Settings.SyncModComponents, "RimMult.SyncModComponentsHint".Translate());
        list.Label("RimMult.ModListHash".Translate(ModList.ComputeHash()));
        list.Label("RimMult.ModListHashHint".Translate());
        list.End();
    }
}

public sealed class RimMultSettings : ModSettings
{
    public string LastServerAddress = "";
    public string HostServerName = "";
    public int HostMaxPlayers = 10;
    public bool HostOpenPort;
    public int HostPort = ProtocolInfo.DefaultPort;
    public Shared.Coop.GameMode HostMode = Shared.Coop.GameMode.SeparateColonies;
    public bool HostAllowPvp = true;

    /// <summary>Co-op host: stream mods' game/world/map components to guests when they change.</summary>
    public bool SyncModComponents = true;

    /// <summary>Lower-case package ids of mods left out of the mod comparison (visual / interface only).</summary>
    public List<string> ClientOnlyMods = ModList.DefaultClientOnly.ToList();

    public override void ExposeData()
    {
        Scribe_Collections.Look(ref ClientOnlyMods, "clientOnlyMods", LookMode.Value);
        ClientOnlyMods ??= ModList.DefaultClientOnly.ToList();
        Scribe_Values.Look(ref LastServerAddress, "lastServerAddress", "");
        Scribe_Values.Look(ref HostServerName, "hostServerName", "");
        Scribe_Values.Look(ref HostMaxPlayers, "hostMaxPlayers", 10);
        Scribe_Values.Look(ref HostOpenPort, "hostOpenPort");
        Scribe_Values.Look(ref HostPort, "hostPort", ProtocolInfo.DefaultPort);
        Scribe_Values.Look(ref HostMode, "hostMode");
        Scribe_Values.Look(ref HostAllowPvp, "hostAllowPvp", true);
        Scribe_Values.Look(ref SyncModComponents, "syncModComponents", true);
    }
}

[DefOf]
public static class RimMultDefOf
{
    public static KeyBindingDef RimMult_ToggleChat = null!;
    public static WorldObjectDef RimMult_RemoteColony = null!;
}
