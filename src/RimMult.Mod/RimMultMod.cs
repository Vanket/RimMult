using HarmonyLib;
using RimMult.Shared;
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
        Log.Message($"[RimMult] {content.ModMetaData.ModVersion} loaded, protocol v{ProtocolInfo.Version}");
    }

    public static RimMultMod Instance { get; private set; } = null!;

    public RimMultSettings Settings { get; }

    public override string SettingsCategory() => "RimMult";

    public override void DoSettingsWindowContents(Rect inRect)
    {
        var list = new Listing_Standard();
        list.Begin(inRect);
        list.Label("Server address (host:port)");
        Settings.LastServerAddress = list.TextEntry(Settings.LastServerAddress);
        list.Gap();
        list.Label($"Mod list hash: {ModList.ComputeHash()}");
        list.End();
    }
}

public sealed class RimMultSettings : ModSettings
{
    public string LastServerAddress = $"127.0.0.1:{ProtocolInfo.DefaultPort}";

    public override void ExposeData()
    {
        Scribe_Values.Look(ref LastServerAddress, "lastServerAddress", $"127.0.0.1:{ProtocolInfo.DefaultPort}");
    }
}
