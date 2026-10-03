using System;
using System.Runtime.InteropServices;
using System.Text;

namespace RimMult.Shared.Serialization;

/// <summary>
/// Compact little-endian binary writer. Integers that are usually small (lengths, ids, counts)
/// go through <see cref="WriteVarUInt"/> / <see cref="WriteVarInt"/> to keep packets short.
/// </summary>
public sealed class ByteWriter
{
    private byte[] _buffer;
    private int _length;

    public ByteWriter(int capacity = 256)
    {
        _buffer = new byte[Math.Max(capacity, 16)];
    }

    public int Length => _length;

    public void Reset() => _length = 0;

    public byte[] ToArray()
    {
        var result = new byte[_length];
        Buffer.BlockCopy(_buffer, 0, result, 0, _length);
        return result;
    }

    public void WriteByte(byte value)
    {
        Ensure(1);
        _buffer[_length++] = value;
    }

    public void WriteBool(bool value) => WriteByte(value ? (byte)1 : (byte)0);

    public void WriteUInt16(ushort value)
    {
        Ensure(2);
        _buffer[_length++] = (byte)value;
        _buffer[_length++] = (byte)(value >> 8);
    }

    public void WriteInt32(int value)
    {
        Ensure(4);
        _buffer[_length++] = (byte)value;
        _buffer[_length++] = (byte)(value >> 8);
        _buffer[_length++] = (byte)(value >> 16);
        _buffer[_length++] = (byte)(value >> 24);
    }

    public void WriteInt64(long value)
    {
        WriteInt32((int)value);
        WriteInt32((int)(value >> 32));
    }

    public void WriteUInt64(ulong value) => WriteInt64((long)value);

    public void WriteFloat(float value)
    {
        var bits = new FloatBits { Float = value };
        WriteInt32(bits.Int);
    }

    public void WriteVarUInt(ulong value)
    {
        Ensure(10);
        while (value >= 0x80)
        {
            _buffer[_length++] = (byte)(value | 0x80);
            value >>= 7;
        }
        _buffer[_length++] = (byte)value;
    }

    /// <summary>Zig-zag encoded so small negative numbers stay short.</summary>
    public void WriteVarInt(long value) => WriteVarUInt((ulong)((value << 1) ^ (value >> 63)));

    /// <summary>Length-prefixed UTF-8. Null is encoded distinctly from the empty string.</summary>
    public void WriteString(string? value)
    {
        if (value == null)
        {
            WriteVarUInt(0);
            return;
        }

        var byteCount = Encoding.UTF8.GetByteCount(value);
        WriteVarUInt((ulong)byteCount + 1);
        Ensure(byteCount);
        _length += Encoding.UTF8.GetBytes(value, 0, value.Length, _buffer, _length);
    }

    public void WriteBytes(byte[] value)
    {
        WriteVarUInt((ulong)value.Length);
        Ensure(value.Length);
        Buffer.BlockCopy(value, 0, _buffer, _length, value.Length);
        _length += value.Length;
    }

    private void Ensure(int extra)
    {
        var needed = _length + extra;
        if (needed <= _buffer.Length)
            return;

        var newSize = _buffer.Length * 2;
        while (newSize < needed)
            newSize *= 2;
        Array.Resize(ref _buffer, newSize);
    }

    [StructLayout(LayoutKind.Explicit)]
    internal struct FloatBits
    {
        [FieldOffset(0)] public float Float;
        [FieldOffset(0)] public int Int;
    }
}
