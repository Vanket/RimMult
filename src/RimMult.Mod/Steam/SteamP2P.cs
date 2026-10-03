using Steamworks;

namespace RimMult.Steam;

/// <summary>Thin wrapper over Steam's P2P packet API (relayed through Steam, so nobody needs to open ports).</summary>
internal static class SteamP2P
{
    /// <summary>Seconds between keep-alives on an otherwise quiet connection.</summary>
    public const float HeartbeatInterval = 2f;

    /// <summary>Seconds of silence after which a peer is considered gone.</summary>
    public const float Timeout = Shared.ProtocolInfo.ConnectionTimeoutSeconds;

    public static void Send(CSteamID target, byte[] frame, bool reliable) =>
        SteamNetworking.SendP2PPacket(
            target,
            frame,
            (uint)frame.Length,
            reliable ? EP2PSend.k_EP2PSendReliable : EP2PSend.k_EP2PSendUnreliable);

    /// <summary>Reads the next packet into <paramref name="buffer"/> (grown as needed).</summary>
    public static bool TryRead(ref byte[] buffer, out int length, out CSteamID remote)
    {
        length = 0;
        remote = default;
        if (!SteamNetworking.IsP2PPacketAvailable(out var size))
            return false;
        if (buffer.Length < size)
            buffer = new byte[size];
        if (!SteamNetworking.ReadP2PPacket(buffer, size, out var read, out remote))
            return false;
        length = (int)read;
        return true;
    }
}
