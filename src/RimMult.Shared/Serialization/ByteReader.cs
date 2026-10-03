using System;
using System.Text;

namespace RimMult.Shared.Serialization;

/// <summary>Counterpart of <see cref="ByteWriter"/>. Every read is bounds-checked; bad input throws <see cref="ProtocolException"/>.</summary>
public sealed class ByteReader
{
    /// <summary>Upper bound for a single string or byte blob, to stop a hostile peer from making us allocate gigabytes.</summary>
    public const int MaxBlobLength = 64 * 1024 * 1024;

    private readonly byte[] _data;
    private readonly int _end;
    private int _position;

    public ByteReader(byte[] data) : this(data, 0, data.Length)
    {
    }

    public ByteReader(byte[] data, int offset, int count)
    {
        if (offset < 0 || count < 0 || offset + count > data.Length)
            throw new ArgumentOutOfRangeException(nameof(count));
        _data = data;
        _position = offset;
        _end = offset + count;
    }

    public int Remaining => _end - _position;

    public byte ReadByte()
    {
        Require(1);
        return _data[_position++];
    }

    public bool ReadBool() => ReadByte() switch
    {
        0 => false,
        1 => true,
        var b => throw new ProtocolException($"Invalid bool value {b}"),
    };

    public ushort ReadUInt16()
    {
        Require(2);
        var value = (ushort)(_data[_position] | (_data[_position + 1] << 8));
        _position += 2;
        return value;
    }

    public int ReadInt32()
    {
        Require(4);
        var value = _data[_position]
                    | (_data[_position + 1] << 8)
                    | (_data[_position + 2] << 16)
                    | (_data[_position + 3] << 24);
        _position += 4;
        return value;
    }

    public long ReadInt64()
    {
        var low = (uint)ReadInt32();
        var high = (long)ReadInt32();
        return (high << 32) | low;
    }

    public ulong ReadUInt64() => (ulong)ReadInt64();

    public float ReadFloat()
    {
        var bits = new ByteWriter.FloatBits { Int = ReadInt32() };
        return bits.Float;
    }

    public ulong ReadVarUInt()
    {
        ulong result = 0;
        for (var shift = 0; shift < 70; shift += 7)
        {
            var b = ReadByte();
            result |= (ulong)(b & 0x7F) << shift;
            if ((b & 0x80) == 0)
                return result;
        }
        throw new ProtocolException("VarUInt is too long");
    }

    public long ReadVarInt()
    {
        var raw = ReadVarUInt();
        return (long)(raw >> 1) ^ -(long)(raw & 1);
    }

    public string? ReadString()
    {
        var prefix = ReadVarUInt();
        if (prefix == 0)
            return null;

        var length = CheckLength(prefix - 1);
        Require(length);
        var value = Encoding.UTF8.GetString(_data, _position, length);
        _position += length;
        return value;
    }

    /// <summary>Like <see cref="ReadString"/> but treats null as a protocol violation.</summary>
    public string ReadRequiredString() => ReadString() ?? throw new ProtocolException("Unexpected null string");

    public byte[] ReadBytes()
    {
        var length = CheckLength(ReadVarUInt());
        Require(length);
        var value = new byte[length];
        Buffer.BlockCopy(_data, _position, value, 0, length);
        _position += length;
        return value;
    }

    public void EnsureFullyRead()
    {
        if (Remaining != 0)
            throw new ProtocolException($"{Remaining} trailing bytes");
    }

    private static int CheckLength(ulong length)
    {
        if (length > MaxBlobLength)
            throw new ProtocolException($"Blob length {length} exceeds limit");
        return (int)length;
    }

    private void Require(int count)
    {
        if (_end - _position < count)
            throw new ProtocolException($"Unexpected end of data (need {count}, have {_end - _position})");
    }
}
