using System;

namespace RimMult.Shared.Time;

/// <summary>Mirrors RimWorld's <c>TimeSpeed</c>; numeric values must stay stable, they go over the wire.</summary>
public enum GameSpeed : byte
{
    Paused = 0,
    Normal = 1,
    Fast = 2,
    Superfast = 3,
    Ultrafast = 4,
}

public static class GameSpeedExtensions
{
    /// <summary>Ticks per real second the game targets at this speed (vanilla multipliers 1/3/6/15).</summary>
    public static int TicksPerSecond(this GameSpeed speed) => speed switch
    {
        GameSpeed.Paused => 0,
        GameSpeed.Normal => ProtocolInfo.TicksPerSecondAtNormal,
        GameSpeed.Fast => ProtocolInfo.TicksPerSecondAtNormal * 3,
        GameSpeed.Superfast => ProtocolInfo.TicksPerSecondAtNormal * 6,
        GameSpeed.Ultrafast => ProtocolInfo.TicksPerSecondAtNormal * 15,
        _ => throw new ArgumentOutOfRangeException(nameof(speed), speed, null),
    };

    public static bool IsDefined(this GameSpeed speed) => speed <= GameSpeed.Ultrafast;
}
