using System.Collections.Generic;
using RimMult.Shared.Serialization;
using RimMult.Shared.World;

namespace RimMult.Shared.Packets;

/// <summary>
/// Client → server: "I generated a planet for my new colony; make it the shared world".
/// Only accepted while the server has no world yet.
/// </summary>
public sealed class WorldCreate : IPacket
{
    public WorldDefinition Definition { get; set; } = new();

    /// <summary>The creator's current absolute tick, which becomes the world clock.</summary>
    public long Tick { get; set; }

    public PacketType Type => PacketType.WorldCreate;

    public void Write(ByteWriter writer)
    {
        Definition.Write(writer);
        writer.WriteVarInt(Tick);
    }

    public static WorldCreate Read(ByteReader reader) => new()
    {
        Definition = WorldDefinition.Read(reader),
        Tick = reader.ReadVarInt(),
    };
}

/// <summary>Server → clients: the world and everyone's colonies. Sent on join and whenever it changes.</summary>
public sealed class WorldUpdate : IPacket
{
    public WorldDefinition? Definition { get; set; }
    public List<ColonyInfo> Colonies { get; set; } = new();

    public PacketType Type => PacketType.WorldUpdate;

    public void Write(ByteWriter writer)
    {
        writer.WriteBool(Definition != null);
        Definition?.Write(writer);
        ColonyInfo.WriteList(writer, Colonies);
    }

    public static WorldUpdate Read(ByteReader reader) => new()
    {
        Definition = reader.ReadBool() ? WorldDefinition.Read(reader) : null,
        Colonies = ColonyInfo.ReadList(reader),
    };
}

/// <summary>
/// Client → server: "I am now playing a save of this world" — the client becomes a simulation authority.
/// The server answers with <see cref="WorldClock"/>.
/// </summary>
public sealed class EnterWorld : IPacket
{
    public string WorldId { get; set; } = "";

    public PacketType Type => PacketType.EnterWorld;

    public void Write(ByteWriter writer) => writer.WriteString(WorldId);

    public static EnterWorld Read(ByteReader reader) => new() { WorldId = reader.ReadRequiredString() };
}

/// <summary>Client → server: back to the main menu (or another save); no longer simulating.</summary>
public sealed class LeaveWorld : IPacket
{
    public PacketType Type => PacketType.LeaveWorld;

    public void Write(ByteWriter writer)
    {
    }

    public static LeaveWorld Read(ByteReader reader) => new();
}

/// <summary>
/// Server → client, answering <see cref="EnterWorld"/>: the current world time. The client shifts its calendar to
/// it, so a colony that was offline (or just founded) continues at the shared date.
/// </summary>
public sealed class WorldClock : IPacket
{
    public long Tick { get; set; }

    public PacketType Type => PacketType.WorldClock;

    public void Write(ByteWriter writer) => writer.WriteVarInt(Tick);

    public static WorldClock Read(ByteReader reader) => new() { Tick = reader.ReadVarInt() };
}

/// <summary>Client → server: the full list of this player's colonies (replaces what the server had for them).</summary>
public sealed class MyColonies : IPacket
{
    public const int MaxColonies = 50;

    /// <summary>Only <see cref="ColonyInfo.Name"/> and <see cref="ColonyInfo.Tile"/> are used; the server fills in the owner.</summary>
    public List<ColonyInfo> Colonies { get; set; } = new();

    public PacketType Type => PacketType.MyColonies;

    public void Write(ByteWriter writer) => ColonyInfo.WriteList(writer, Colonies);

    public static MyColonies Read(ByteReader reader)
    {
        var packet = new MyColonies { Colonies = ColonyInfo.ReadList(reader) };
        if (packet.Colonies.Count > MaxColonies)
            throw new ProtocolException($"Too many colonies: {packet.Colonies.Count}");
        return packet;
    }
}
