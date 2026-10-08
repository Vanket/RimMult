using System;
using System.Collections.Generic;
using RimMult.Shared.Serialization;

namespace RimMult.Shared.World;

/// <summary>
/// Everything needed to generate the planet on every machine: the seed and parameters. Mods (or a planet generated
/// long ago with other mods) can still make it come out differently, so the creator's planet itself follows
/// (<see cref="WorldState.TerrainData"/>) and every game takes it over.
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

    /// <summary>The owner's color (index into the player palette); filled in by the server.</summary>
    public byte ColorIndex { get; set; }

    public void Write(ByteWriter writer)
    {
        writer.WriteUInt64(OwnerSteamId);
        writer.WriteString(OwnerName);
        writer.WriteString(Name);
        writer.WriteString(Tile);
        writer.WriteByte(ColorIndex);
    }

    /// <param name="withColor">False for world files older than format 3, which had no color.</param>
    public static ColonyInfo Read(ByteReader reader, bool withColor = true) => new()
    {
        OwnerSteamId = reader.ReadUInt64(),
        OwnerName = reader.ReadRequiredString(),
        Name = reader.ReadRequiredString(),
        Tile = reader.ReadRequiredString(),
        ColorIndex = withColor ? reader.ReadByte() : (byte)0,
    };

    public static void WriteList(ByteWriter writer, IReadOnlyList<ColonyInfo> colonies)
    {
        writer.WriteVarUInt((ulong)colonies.Count);
        foreach (var colony in colonies)
            colony.Write(writer);
    }

    public static List<ColonyInfo> ReadList(ByteReader reader, bool withColor = true)
    {
        var count = reader.ReadVarUInt();
        if (count > 10_000)
            throw new ProtocolException($"Too many colonies: {count}");
        var colonies = new List<ColonyInfo>((int)count);
        for (var i = 0UL; i < count; i++)
            colonies.Add(Read(reader, withColor));
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
    /// <summary>
    /// 1: world, clock, colonies, mods. 2: + parcels in transit. 3: + player colors, destroyed NPC settlements.
    /// 4: + relations between players. 5: + the chronicle and players' stats. 6: + the NPC settlements and the creator.
    /// 7: + treaties, players' reputation. 8: + the market (lots and orders). 9: + NPC deals on the market, the world's prices.
    /// 10: + the planet (the creator's tiles, rivers and roads).
    /// </summary>
    private const byte FormatVersion = 10;

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

    /// <summary>Each player's color (owner key → palette index), stable across sessions.</summary>
    public Dictionary<ulong, byte> PlayerColors { get; set; } = new();

    /// <summary>Tiles of NPC settlements that some player destroyed; they are gone for everyone.</summary>
    public List<string> DestroyedSettlements { get; set; } = new();

    /// <summary>Wars and alliances between players (pairs not listed are neutral).</summary>
    public List<RelationEntry> Relations { get; set; } = new();

    /// <summary>What happened in the world, oldest first (at most <see cref="World.Chronicle.MaxEntries"/>).</summary>
    public List<ChronicleEntry> Chronicle { get; set; } = new();

    /// <summary>Each player's standing (owner key → stats), for the chronicle's table.</summary>
    public Dictionary<ulong, PlayerStats> Stats { get; set; } = new();

    /// <summary>Where the world's NPC settlements are (the creator's layout); empty until someone allowed to tells.</summary>
    public List<NpcSettlement> NpcSettlements { get; set; } = new();

    /// <summary>Owner key of the player who created the world (0: unknown, worlds older than format 6).</summary>
    public ulong CreatorOwner { get; set; }

    /// <summary>Pacts, truces and tributes between players.</summary>
    public List<Treaty> Treaties { get; set; } = new();

    public long NextTreatyId { get; set; } = 1;

    /// <summary>Goods for sale on the world's market.</summary>
    public List<MarketLot> MarketLots { get; set; } = new();

    /// <summary>Open orders on the world's market.</summary>
    public List<MarketOrder> MarketOrders { get; set; } = new();

    public long NextMarketId { get; set; } = 1;

    /// <summary>The world's prices (moved by deals with NPC factions and by news).</summary>
    public MarketPrices Prices { get; set; } = new();

    /// <summary>The planet as the world's creator has it (packed by the game; see <see cref="Packets.WorldTerrain"/>); empty until it is sent.</summary>
    public byte[] TerrainData { get; set; } = Array.Empty<byte>();

    /// <summary>Fingerprint of <see cref="TerrainData"/>; empty while there is none.</summary>
    public string TerrainHash { get; set; } = "";

    public void Write(ByteWriter writer) => Write(writer, withTerrain: true);

    private void Write(ByteWriter writer, bool withTerrain)
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
        writer.WriteVarUInt((ulong)PlayerColors.Count);
        foreach (var pair in PlayerColors)
        {
            writer.WriteUInt64(pair.Key);
            writer.WriteByte(pair.Value);
        }
        WriteStrings(writer, DestroyedSettlements);
        RelationEntry.WriteList(writer, Relations);
        ChronicleEntry.WriteList(writer, Chronicle);
        PlayerStats.WriteList(writer, new List<PlayerStats>(Stats.Values));
        NpcSettlement.WriteList(writer, NpcSettlements);
        writer.WriteUInt64(CreatorOwner);
        Treaty.WriteList(writer, Treaties);
        writer.WriteVarInt(NextTreatyId);
        MarketLot.WriteList(writer, MarketLots, withPayload: true);
        MarketOrder.WriteList(writer, MarketOrders, withPayload: true);
        writer.WriteVarInt(NextMarketId);
        Prices.Write(writer);
        writer.WriteString(withTerrain ? TerrainHash : "");
        writer.WriteBytes(withTerrain ? TerrainData : Array.Empty<byte>());
    }

    public static void WriteStrings(ByteWriter writer, IReadOnlyList<string> values)
    {
        writer.WriteVarUInt((ulong)values.Count);
        foreach (var value in values)
            writer.WriteString(value);
    }

    public static List<string> ReadStrings(ByteReader reader)
    {
        var count = reader.ReadVarUInt();
        if (count > 100_000)
            throw new ProtocolException($"Too many entries: {count}");
        var values = new List<string>((int)count);
        for (var i = 0UL; i < count; i++)
            values.Add(reader.ReadRequiredString());
        return values;
    }

    private static WorldState Read(ByteReader reader, byte version)
    {
        var state = new WorldState
        {
            Definition = reader.ReadBool() ? WorldDefinition.Read(reader) : null,
            Tick = reader.ReadVarInt(),
            Colonies = ColonyInfo.ReadList(reader, withColor: version >= 3),
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
        if (version >= 3)
        {
            var colors = reader.ReadVarUInt();
            if (colors > 100_000)
                throw new ProtocolException($"Too many players: {colors}");
            for (var i = 0UL; i < colors; i++)
                state.PlayerColors[reader.ReadUInt64()] = reader.ReadByte();
            state.DestroyedSettlements = ReadStrings(reader);
        }
        if (version >= 4)
            state.Relations = RelationEntry.ReadList(reader);
        if (version >= 5)
        {
            state.Chronicle = ChronicleEntry.ReadList(reader);
            foreach (var stats in PlayerStats.ReadList(reader, withReputation: version >= 7))
                state.Stats[stats.Owner] = stats;
        }
        if (version >= 6)
        {
            state.NpcSettlements = NpcSettlement.ReadList(reader);
            state.CreatorOwner = reader.ReadUInt64();
        }
        if (version >= 7)
        {
            state.Treaties = Treaty.ReadList(reader);
            state.NextTreatyId = reader.ReadVarInt();
        }
        if (version >= 8)
        {
            state.MarketLots = MarketLot.ReadList(reader, withPayload: true, extended: version >= 9);
            state.MarketOrders = MarketOrder.ReadList(reader, withPayload: true, extended: version >= 9);
            state.NextMarketId = reader.ReadVarInt();
        }
        if (version >= 9)
            state.Prices = MarketPrices.Read(reader);
        if (version >= 10)
        {
            state.TerrainHash = reader.ReadRequiredString();
            state.TerrainData = reader.ReadBytes();
        }
        return state;
    }

    /// <summary>
    /// Standalone file/save format, with a version byte in front. Without the planet for an in-game host's save: its
    /// game is the planet, and sends it again when it hosts next time.
    /// </summary>
    public byte[] Serialize(bool withTerrain = true)
    {
        var writer = new ByteWriter();
        writer.WriteByte(FormatVersion);
        Write(writer, withTerrain);
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
