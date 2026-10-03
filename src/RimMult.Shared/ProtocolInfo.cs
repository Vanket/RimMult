namespace RimMult.Shared;

public static class ProtocolInfo
{
    /// <summary>Bumped on every incompatible change to packets or their semantics.</summary>
    public const int Version = 4;

    /// <summary>LiteNetLib connection key; rejects random UDP traffic before the handshake.</summary>
    public const string ConnectionKey = "RimMult";

    public const int DefaultPort = 26480;

    /// <summary>
    /// Seconds of silence before a connection counts as dead. Deliberately long: while RimWorld generates a planet
    /// or loads a map its main thread (and with it our networking) stalls, sometimes for a minute or more, and
    /// that must not drop everyone. Clean disconnects are announced, so only crashes take this long to notice.
    /// </summary>
    public const int ConnectionTimeoutSeconds = 120;

    /// <summary>RimWorld runs 60 ticks per real second at speed x1.</summary>
    public const int TicksPerSecondAtNormal = 60;
}
