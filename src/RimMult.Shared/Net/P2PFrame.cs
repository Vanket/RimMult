using System;
using System.Collections.Generic;
using RimMult.Shared.Serialization;

namespace RimMult.Shared.Net;

public enum P2PFrameKind : byte
{
    /// <summary>
    /// Client → host: open a connection. Re-sent until accepted. Carries a random 4-byte nonce per attempt, so
    /// the host tells a repeat of the same attempt from a fresh reconnect (game restarted) of the same SteamID.
    /// </summary>
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

    /// <summary>
    /// A piece of a reliable message too large for one Steam packet (Steam caps reliable P2P packets at 1 MB).
    /// One flag byte (1 = last piece) then data; pieces arrive in order on the reliable channel.
    /// </summary>
    ReliablePart = 7,
}

/// <summary>
/// Framing for connectionless datagram transports (Steam P2P): Steam only moves packets between
/// SteamIDs, so connection setup, keep-alive and sequencing are layered on top with a 1–3 byte header.
/// </summary>
public static class P2PFrame
{
    public const int SequencedHeaderSize = 3;

    public const int ConnectFrameSize = 5;

    public static byte[] Control(P2PFrameKind kind) => new[] { (byte)kind };

    public static byte[] Connect(uint nonce) => new[]
    {
        (byte)P2PFrameKind.Connect, (byte)nonce, (byte)(nonce >> 8), (byte)(nonce >> 16), (byte)(nonce >> 24),
    };

    /// <summary>The nonce of a parsed <see cref="P2PFrameKind.Connect"/> frame (its payload).</summary>
    public static uint ConnectNonce(byte[] payload) =>
        (uint)(payload[0] | (payload[1] << 8) | (payload[2] << 16) | (payload[3] << 24));

    public static byte[] Reliable(byte[] payload)
    {
        var frame = new byte[payload.Length + 1];
        frame[0] = (byte)P2PFrameKind.Reliable;
        Buffer.BlockCopy(payload, 0, frame, 1, payload.Length);
        return frame;
    }

    /// <summary>Largest reliable chunk per Steam packet, well under Steam's 1 MB limit.</summary>
    public const int MaxReliableChunk = 512 * 1024;

    /// <summary>One <see cref="P2PFrameKind.Reliable"/> frame, or a series of parts for large payloads.</summary>
    public static List<byte[]> ReliableFrames(byte[] payload, int maxChunk = MaxReliableChunk)
    {
        if (payload.Length <= maxChunk)
            return new List<byte[]> { Reliable(payload) };

        var frames = new List<byte[]>();
        for (var offset = 0; offset < payload.Length; offset += maxChunk)
        {
            var count = Math.Min(maxChunk, payload.Length - offset);
            var frame = new byte[count + 2];
            frame[0] = (byte)P2PFrameKind.ReliablePart;
            frame[1] = offset + count >= payload.Length ? (byte)1 : (byte)0;
            Buffer.BlockCopy(payload, offset, frame, 2, count);
            frames.Add(frame);
        }
        return frames;
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
                if (length != ConnectFrameSize)
                    return false;
                payload = Slice(frame, 1, 4);
                return true;
            case P2PFrameKind.Accept:
            case P2PFrameKind.Disconnect:
            case P2PFrameKind.Heartbeat:
                return length == 1;
            case P2PFrameKind.Reliable:
                payload = Slice(frame, 1, length - 1);
                return true;
            case P2PFrameKind.ReliablePart:
                if (length < 2 || frame[1] > 1)
                    return false;
                sequence = frame[1]; // 1 = last part
                payload = Slice(frame, 2, length - 2);
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

/// <summary>Collects <see cref="P2PFrameKind.ReliablePart"/> frames back into one message.</summary>
public sealed class P2PReassembler
{
    public const int MaxMessageBytes = ByteReader.MaxBlobLength;

    private readonly List<byte[]> _parts = new();
    private int _size;

    /// <summary>Adds a part; returns the whole message after the last one, otherwise null.</summary>
    public byte[]? Add(byte[] part, bool last)
    {
        _size += part.Length;
        if (_size > MaxMessageBytes)
        {
            Reset();
            throw new ProtocolException("Fragmented message too large");
        }

        _parts.Add(part);
        if (!last)
            return null;

        var message = new byte[_size];
        var offset = 0;
        foreach (var piece in _parts)
        {
            Buffer.BlockCopy(piece, 0, message, offset, piece.Length);
            offset += piece.Length;
        }
        Reset();
        return message;
    }

    private void Reset()
    {
        _parts.Clear();
        _size = 0;
    }
}
