using System.Collections.Generic;
using RimMult.Shared;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace RimMult.UI;

/// <summary>Player colors: the server hands out palette indices (the first player gets green), everyone sees the same.</summary>
internal static class PlayerPalette
{
    private static readonly Color[] Colors =
    {
        new(0.30f, 0.85f, 0.30f), // green
        new(0.90f, 0.25f, 0.25f), // red
        new(0.25f, 0.55f, 0.95f), // blue
        new(0.95f, 0.85f, 0.20f), // yellow
        new(0.70f, 0.35f, 0.90f), // purple
        new(0.95f, 0.55f, 0.15f), // orange
        new(0.20f, 0.85f, 0.85f), // cyan
        new(0.95f, 0.45f, 0.75f), // pink
        new(0.95f, 0.95f, 0.95f), // white
        new(0.60f, 0.40f, 0.20f), // brown
        new(0.65f, 0.95f, 0.30f), // lime
        new(0.20f, 0.25f, 0.65f), // navy
    };

    private static readonly Dictionary<(string, int), Material> Materials = new();

    static PlayerPalette()
    {
        System.Diagnostics.Debug.Assert(Colors.Length == ProtocolInfo.PlayerPaletteSize);
    }

    public static Color Get(byte index) => Colors[index % Colors.Length];

    public static string Hex(byte index) => "#" + ColorUtility.ToHtmlStringRGB(Get(index));

    /// <summary>A name in the player's color, for rich text labels.</summary>
    public static string Colorize(string name, byte index) => $"<color={Hex(index)}>{name.Replace("<", "‹")}</color>";

    /// <summary>A world-object material tinted with a player's color (cached).</summary>
    public static Material WorldMaterial(string texturePath, byte index)
    {
        if (!Materials.TryGetValue((texturePath, index), out var material))
        {
            material = MaterialPool.MatFrom(texturePath, ShaderDatabase.WorldOverlayTransparentLit, Get(index), WorldMaterials.WorldObjectRenderQueue);
            Materials[(texturePath, index)] = material;
        }
        return material;
    }
}
