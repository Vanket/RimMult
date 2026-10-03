using System;

namespace RimMult.Shared.Net;

public enum P2PFrameKind : byte
{
    /// <summary>Client → host: open a connection. Re-sent until accepted.</summary>
    Connect = 1,

    /// <summary>Host → client: connection accepted.</summary>
    Accept = 2,

    /// <summary>Either side: connection closed.</summary>
    Disconnect = 3,

    /// <summary>Either side: keep-alive, so silent peers can be told apart from dead ones.</summary>
    Heartbeat = 4,

    Reliable = 5,

    /// <summary>Unreliable payload with a sequence number; older ones are dropped on arrival.</summary>
    Sequenced = 6,
}

/// <summary>
/// Framing for connectionless datagram transports (Steam P2P): Steam only moves packets between
/// SteamIDs, so connection setup, keep-alive and sequencing are layered on top with a 1–3 byte header.
/// </summary>
public static class P2PFrame
{
    public const int SequencedHeaderSize = 3;

    public static byte[] Control(P2PFrameKind kind) => new[] { (byte)kind };

    public static byte[] Reliable(byte[] payload)
    {
        var frame = new byte[payload.Length + 1];
        frame[0] = (byte)P2PFrameKind.Reliable;
        Buffer.BlockCopy(payload, 0, frame, 1, payload.Length);
        return frame;
    }

    public static byte[] Sequenced(byte[] payload, ushort sequence)
    {
        var frame = new byte[payload.Length + SequencedHeaderSize];
        frame[0] = (byte)P2PFrameKind.Sequenced;
        frame[1] = (byte)sequence;
        frame[2] = (byte)(sequence >> 8);
        Buffer.BlockCopy(payload, 0, frame, SequencedHeaderSize, payload.Length);
        return frame;
    }

    /// <summary>Splits a received frame. Returns false for frames that are too short or of unknown kind.</summary>
    public static bool TryParse(byte[] frame, int length, out P2PFrameKind kind, out ushort sequence, out byte[] payload)
    {
        kind = default;
        sequence = 0;
        payload = Array.Empty<byte>();
        if (length < 1 || length > frame.Length)
            return false;

        kind = (P2PFrameKind)frame[0];
        switch (kind)
        {
            case P2PFrameKind.Connect:
            case P2PFrameKind.Accept:
            case P2PFrameKind.Disconnect:
            case P2PFrameKind.Heartbeat:
                return length == 1;
            case P2PFrameKind.Reliable:
                payload = Slice(frame, 1, length - 1);
                return true;
            case P2PFrameKind.Sequenced:
                if (length < SequencedHeaderSize)
                    return false;
                sequence = (ushort)(frame[1] | (frame[2] << 8));
                payload = Slice(frame, SequencedHeaderSize, length - SequencedHeaderSize);
                return true;
            default:
                return false;
        }
    }

    /// <summary>Wrap-around aware "a is newer than b" for 16-bit sequence numbers.</summary>
    public static bool IsNewer(ushort a, ushort b) => a != b && (ushort)(a - b) < 0x8000;

    private static byte[] Slice(byte[] source, int offset, int count)
    {
        var result = new byte[count];
        Buffer.BlockCopy(source, offset, result, 0, count);
        return result;
    }
}
