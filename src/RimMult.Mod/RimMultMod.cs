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
        new Harmony(HarmonyId).PatchAll(typeof(RimMultMod).Assembly);

        LongEventHandler.ExecuteWhenFinished(() =>
        {
            var runner = new GameObject("RimMult");
            Object.DontDestroyOnLoad(runner);
            runner.AddComponent<RimMultRunner>();
            SteamIntegration.Init();
        });

        Log.Message($"[RimMult] {content.ModMetaData.ModVersion} loaded, protocol v{ProtocolInfo.Version}");
    }

    public static RimMultMod Instance { get; private set; } = null!;

    public RimMultSettings Settings { get; }

    public override string SettingsCategory() => "RimMult";

    public override void DoSettingsWindowContents(Rect inRect)
    {
        var list = new Listing_Standard();
        list.Begin(inRect);
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

    public override void ExposeData()
    {
        Scribe_Values.Look(ref LastServerAddress, "lastServerAddress", "");
        Scribe_Values.Look(ref HostServerName, "hostServerName", "");
        Scribe_Values.Look(ref HostMaxPlayers, "hostMaxPlayers", 10);
        Scribe_Values.Look(ref HostOpenPort, "hostOpenPort");
        Scribe_Values.Look(ref HostPort, "hostPort", ProtocolInfo.DefaultPort);
    }
}

[DefOf]
public static class RimMultDefOf
{
    public static KeyBindingDef RimMult_ToggleChat = null!;
}
