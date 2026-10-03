namespace RimMult.Shared;

public static class ProtocolInfo
{
    /// <summary>Bumped on every incompatible change to packets or their semantics.</summary>
    public const int Version = 2;

    /// <summary>LiteNetLib connection key; rejects random UDP traffic before the handshake.</summary>
    public const string ConnectionKey = "RimMult";

    public const int DefaultPort = 26480;

    /// <summary>RimWorld runs 60 ticks per real second at speed x1.</summary>
    public const int TicksPerSecondAtNormal = 60;
}
