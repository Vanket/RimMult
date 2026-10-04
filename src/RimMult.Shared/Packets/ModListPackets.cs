using System.Collections.Generic;
using RimMult.Shared.Mods;
using RimMult.Shared.Serialization;

namespace RimMult.Shared.Packets;

/// <summary>
/// Client → server, instead of <see cref="ClientHello"/>: "which mods do you play with?" — to compare (and fix) the
/// mod list before joining. The server answers with <see cref="ServerModList"/> and closes the connection.
/// </summary>
public sealed class ModListQuery : IPacket
{
    public int ProtocolVersion { get; set; } = ProtocolInfo.Version;

    public PacketType Type => PacketType.ModListQuery;

    public void Write(ByteWriter writer) => writer.WriteVarInt(ProtocolVersion);

    public static ModListQuery Read(ByteReader reader) => new() { ProtocolVersion = (int)reader.ReadVarInt() };
}

/// <summary>Server → client, answering <see cref="ModListQuery"/>: what joining takes.</summary>
public sealed class ServerModList : IPacket
{
    public int ProtocolVersion { get; set; } = ProtocolInfo.Version;

    /// <summary>The RimWorld version the server requires (null: nobody has joined yet).</summary>
    public string? GameVersion { get; set; }

    /// <summary>The required mods in load order; null when the server knows only their hash (pinned in its settings).</summary>
    public List<ModEntry>? Mods { get; set; }

    public PacketType Type => PacketType.ServerModList;

    public void Write(ByteWriter writer)
    {
        writer.WriteVarInt(ProtocolVersion);
        writer.WriteString(GameVersion);
        writer.WriteBool(Mods != null);
        if (Mods != null)
            ModEntry.WriteList(writer, Mods);
    }

    public static ServerModList Read(ByteReader reader) => new()
    {
        ProtocolVersion = (int)reader.ReadVarInt(),
        GameVersion = reader.ReadString(),
        Mods = reader.ReadBool() ? ModEntry.ReadList(reader) : null,
    };
}
