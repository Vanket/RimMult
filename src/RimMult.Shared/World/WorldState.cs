using System;
using System.Collections.Generic;
using RimMult.Shared.Serialization;

namespace RimMult.Shared.World;

/// <summary>
/// Everything needed to generate the same planet on every machine. RimWorld's world generation is deterministic
/// for a given seed, parameters and mod list (the mod list is enforced at handshake), so this is sent instead of
/// the planet itself.
/// </summary>
public sealed class WorldDefinition
{
    /// <summary>Unique per multiplayer world; stored in each player's save to recognise it.</summary>
    public string WorldId { get; set; } = "";
    public string SeedString { get; set; } = "";
    public float PlanetCoverage { get; set; }
    public byte Rainfall { get; set; }
    public byte Temperature { get; set; }
    public byte Population { get; set; }
    public byte LandmarkDensity { get; set; }
    public float Pollution { get; set; }

    /// <summary>Faction def names used at world generation (repeats allowed). Empty: the game's defaults.</summary>
    public List<string> Factions { get; set; } = new();

    public void Write(ByteWriter writer)
    {
        writer.WriteString(WorldId);
        writer.WriteString(SeedString);
        writer.WriteFloat(PlanetCoverage);
        writer.WriteByte(Rainfall);
        writer.WriteByte(Temperature);
        writer.WriteByte(Population);
        writer.WriteByte(LandmarkDensity);
        writer.WriteFloat(Pollution);
        writer.WriteVarUInt((ulong)Factions.Count);
        foreach (var faction in Factions)
            writer.WriteString(faction);
    }

    public static WorldDefinition Read(ByteReader reader)
    {
        var definition = new WorldDefinition
        {
            WorldId = reader.ReadRequiredString(),
            SeedString = reader.ReadRequiredString(),
            PlanetCoverage = reader.ReadFloat(),
            Rainfall = reader.ReadByte(),
            Temperature = reader.ReadByte(),
            Population = reader.ReadByte(),
            LandmarkDensity = reader.ReadByte(),
            Pollution = reader.ReadFloat(),
        };
        var count = reader.ReadVarUInt();
        if (count > 1000)
            throw new ProtocolException($"Too many factions: {count}");
        for (var i = 0UL; i < count; i++)
            definition.Factions.Add(reader.ReadRequiredString());
        if (definition.WorldId.Length == 0)
            throw new ProtocolException("World without id");
        return definition;
    }
}

/// <summary>A player's colony as other players see it on their globe.</summary>
public sealed class ColonyInfo
{
    public ulong OwnerSteamId { get; set; }
    public string OwnerName { get; set; } = "";
    public string Name { get; set; } = "";

    /// <summary>World tile in RimWorld's <c>PlanetTile</c> string form (tile id plus planet layer).</summary>
    public string Tile { get; set; } = "";

    public void Write(ByteWriter writer)
    {
        writer.WriteUInt64(OwnerSteamId);
        writer.WriteString(OwnerName);
        writer.WriteString(Name);
        writer.WriteString(Tile);
    }

    public static ColonyInfo Read(ByteReader reader) => new()
    {
        OwnerSteamId = reader.ReadUInt64(),
        OwnerName = reader.ReadRequiredString(),
        Name = reader.ReadRequiredString(),
        Tile = reader.ReadRequiredString(),
    };

    public static void WriteList(ByteWriter writer, IReadOnlyList<ColonyInfo> colonies)
    {
        writer.WriteVarUInt((ulong)colonies.Count);
        foreach (var colony in colonies)
            colony.Write(writer);
    }

    public static List<ColonyInfo> ReadList(ByteReader reader)
    {
        var count = reader.ReadVarUInt();
        if (count > 10_000)
            throw new ProtocolException($"Too many colonies: {count}");
        var colonies = new List<ColonyInfo>((int)count);
        for (var i = 0UL; i < count; i++)
            colonies.Add(Read(reader));
        return colonies;
    }
}

/// <summary>
/// A parcel of items on its way from one player's colony to another's. The server keeps it until the recipient
/// confirms delivery, so it survives the recipient being offline, disconnects and server restarts.
/// </summary>
public sealed class MailItem
{
    public const int MaxPayloadBytes = 16 * 1024 * 1024;

    public long Id { get; set; }
    public ulong FromOwner { get; set; }
    public string FromName { get; set; } = "";
    public ulong ToOwner { get; set; }

    /// <summary>Colony tile it was sent to (PlanetTile string); the recipient drops it there if it can.</summary>
    public string ToTile { get; set; } = "";

    /// <summary>Human-readable contents, e.g. "Steel x200, Medicine x10".</summary>
    public string Summary { get; set; } = "";

    /// <summary>The recipient no longer had a colony there; the parcel came back to its sender.</summary>
    public bool Returned { get; set; }

    /// <summary>The items, serialized by the game (opaque to the server).</summary>
    public byte[] Payload { get; set; } = Array.Empty<byte>();

    public void Write(ByteWriter writer)
    {
        writer.WriteVarInt(Id);
        writer.WriteUInt64(FromOwner);
        writer.WriteString(FromName);
        writer.WriteUInt64(ToOwner);
        writer.WriteString(ToTile);
        writer.WriteString(Summary);
        writer.WriteBool(Returned);
        writer.WriteBytes(Payload);
    }

    public static MailItem Read(ByteReader reader) => new()
    {
        Id = reader.ReadVarInt(),
        FromOwner = reader.ReadUInt64(),
        FromName = reader.ReadRequiredString(),
        ToOwner = reader.ReadUInt64(),
        ToTile = reader.ReadRequiredString(),
        Summary = reader.ReadRequiredString(),
        Returned = reader.ReadBool(),
        Payload = reader.ReadBytes(),
    };
}

/// <summary>The server's view of the shared world. Persisted by the dedicated server and by an in-game host's save.</summary>
public sealed class WorldState
{
    /// <summary>1: world, clock, colonies, mods. 2: + parcels in transit.</summary>
    private const byte FormatVersion = 2;

    /// <summary>Null until the first player creates the world (picks the planet when starting their colony).</summary>
    public WorldDefinition? Definition { get; set; }

    /// <summary>World clock in RimWorld absolute ticks; frozen while nobody is playing.</summary>
    public long Tick { get; set; }

    public List<ColonyInfo> Colonies { get; set; } = new();

    /// <summary>
    /// Mod list and game version the world was created with. A world only makes sense with the mods it was
    /// generated with, so after a restart these are enforced instead of trusting whoever joins first.
    /// </summary>
    public string? ModListHash { get; set; }
    public string? GameVersion { get; set; }

    /// <summary>Parcels not yet confirmed by their recipient.</summary>
    public List<MailItem> Mail { get; set; } = new();

    public long NextMailId { get; set; } = 1;

    public void Write(ByteWriter writer)
    {
        writer.WriteBool(Definition != null);
        Definition?.Write(writer);
        writer.WriteVarInt(Tick);
        ColonyInfo.WriteList(writer, Colonies);
        writer.WriteString(ModListHash);
        writer.WriteString(GameVersion);
        writer.WriteVarInt(NextMailId);
        writer.WriteVarUInt((ulong)Mail.Count);
        foreach (var item in Mail)
            item.Write(writer);
    }

    private static WorldState Read(ByteReader reader, byte version)
    {
        var state = new WorldState
        {
            Definition = reader.ReadBool() ? WorldDefinition.Read(reader) : null,
            Tick = reader.ReadVarInt(),
            Colonies = ColonyInfo.ReadList(reader),
            ModListHash = reader.ReadString(),
            GameVersion = reader.ReadString(),
        };
        if (version >= 2)
        {
            state.NextMailId = reader.ReadVarInt();
            var count = reader.ReadVarUInt();
            if (count > 100_000)
                throw new ProtocolException($"Too many parcels: {count}");
            for (var i = 0UL; i < count; i++)
                state.Mail.Add(MailItem.Read(reader));
        }
        return state;
    }

    /// <summary>Standalone file/save format, with a version byte in front.</summary>
    public byte[] Serialize()
    {
        var writer = new ByteWriter();
        writer.WriteByte(FormatVersion);
        Write(writer);
        return writer.ToArray();
    }

    public static WorldState Deserialize(byte[] data)
    {
        var reader = new ByteReader(data);
        var version = reader.ReadByte();
        if (version < 1 || version > FormatVersion)
            throw new ProtocolException($"Unsupported world file version {version}");
        var state = Read(reader, version);
        reader.EnsureFullyRead();
        return state;
    }
}
