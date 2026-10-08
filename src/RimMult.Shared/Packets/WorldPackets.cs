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

    /// <summary>NPC settlements destroyed by any player (their tiles); removed on everyone's globe.</summary>
    public List<string> DestroyedSettlements { get; set; } = new();

    /// <summary>Wars and alliances between players.</summary>
    public List<RelationEntry> Relations { get; set; } = new();

    /// <summary>Pacts, truces and tributes between players.</summary>
    public List<Treaty> Treaties { get; set; } = new();

    public PacketType Type => PacketType.WorldUpdate;

    public void Write(ByteWriter writer)
    {
        writer.WriteBool(Definition != null);
        Definition?.Write(writer);
        ColonyInfo.WriteList(writer, Colonies);
        WorldState.WriteStrings(writer, DestroyedSettlements);
        RelationEntry.WriteList(writer, Relations);
        Treaty.WriteList(writer, Treaties);
    }

    public static WorldUpdate Read(ByteReader reader) => new()
    {
        Definition = reader.ReadBool() ? WorldDefinition.Read(reader) : null,
        Colonies = ColonyInfo.ReadList(reader),
        DestroyedSettlements = WorldState.ReadStrings(reader),
        Relations = RelationEntry.ReadList(reader),
        Treaties = Treaty.ReadList(reader),
    };
}

/// <summary>Client → server: an NPC settlement on this tile was destroyed in my game.</summary>
public sealed class SettlementDestroyed : IPacket
{
    public string Tile { get; set; } = "";

    public PacketType Type => PacketType.SettlementDestroyed;

    public void Write(ByteWriter writer) => writer.WriteString(Tile);

    public static SettlementDestroyed Read(ByteReader reader) => new() { Tile = reader.ReadRequiredString() };
}

/// <summary>
/// A message between two players, passed through the server unchanged (trade negotiation and the like).
/// Client → server: <see cref="PlayerId"/> is the recipient; server → client: the sender. Only between players
/// who are both in the world.
/// </summary>
public sealed class PlayerRelay : IPacket
{
    public const int MaxDataBytes = 256 * 1024;

    public int PlayerId { get; set; }
    public RelayChannel Channel { get; set; }
    public byte[] Data { get; set; } = System.Array.Empty<byte>();

    public PacketType Type => PacketType.PlayerRelay;

    public void Write(ByteWriter writer)
    {
        writer.WriteVarInt(PlayerId);
        writer.WriteByte((byte)Channel);
        writer.WriteBytes(Data);
    }

    public static PlayerRelay Read(ByteReader reader)
    {
        var relay = new PlayerRelay
        {
            PlayerId = (int)reader.ReadVarInt(),
            Channel = (RelayChannel)reader.ReadByte(),
            Data = reader.ReadBytes(),
        };
        if (relay.Data.Length > MaxDataBytes)
            throw new ProtocolException("Relay message too large");
        return relay;
    }
}

public enum RelayChannel : byte
{
    Trade = 1,
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

    /// <summary>The server knows the world's NPC settlements (they follow as <see cref="NpcLayout"/>); if not, it asks for them.</summary>
    public bool HasNpcLayout { get; set; }

    /// <summary>Fingerprint of the world's planet the server keeps (<see cref="WorldTerrain"/>); empty while it has none.</summary>
    public string TerrainHash { get; set; } = "";

    /// <summary>The server has no planet yet and would take this player's: send it.</summary>
    public bool WantsTerrain { get; set; }

    public PacketType Type => PacketType.WorldClock;

    public void Write(ByteWriter writer)
    {
        writer.WriteVarInt(Tick);
        writer.WriteBool(HasNpcLayout);
        writer.WriteString(TerrainHash);
        writer.WriteBool(WantsTerrain);
    }

    public static WorldClock Read(ByteReader reader) => new()
    {
        Tick = reader.ReadVarInt(),
        HasNpcLayout = reader.ReadBool(),
        TerrainHash = reader.ReadRequiredString(),
        WantsTerrain = reader.ReadBool(),
    };
}

/// <summary>Client → server: send me the world's planet (<see cref="WorldTerrain"/>), if you have it.</summary>
public sealed class TerrainRequest : IPacket
{
    public PacketType Type => PacketType.TerrainRequest;

    public void Write(ByteWriter writer)
    {
    }

    public static TerrainRequest Read(ByteReader reader) => new();
}

/// <summary>
/// The world's planet as its creator's game has it: every tile's biome, height, hills, climate, rivers, roads and
/// features, packed by the game (opaque to the server). Each game generates the planet from the seed itself, and mods
/// can make that come out differently, so the creator's planet is sent and every game takes it over.
/// Client → server: from the world's creator (or the in-game host), taken only while the server has none yet.
/// Server → client: the planet, on request; with empty <see cref="Data"/> just the news that it is there
/// (<see cref="Hash"/>), so a game that already has it doesn't download it again.
/// </summary>
public sealed class WorldTerrain : IPacket
{
    public const int MaxDataBytes = 32 * 1024 * 1024;

    public string Hash { get; set; } = "";

    public byte[] Data { get; set; } = System.Array.Empty<byte>();

    public PacketType Type => PacketType.WorldTerrain;

    public void Write(ByteWriter writer)
    {
        writer.WriteString(Hash);
        writer.WriteBytes(Data);
    }

    public static WorldTerrain Read(ByteReader reader)
    {
        var terrain = new WorldTerrain { Hash = reader.ReadRequiredString(), Data = reader.ReadBytes() };
        if (terrain.Data.Length > MaxDataBytes)
            throw new ProtocolException("Planet data too large");
        return terrain;
    }

    /// <summary>The fingerprint of packed planet data: the same bytes, the same hash, in every game and on the server.</summary>
    public static string HashOf(byte[] data)
    {
        using var sha = System.Security.Cryptography.SHA256.Create();
        var hash = sha.ComputeHash(data);
        var hex = new System.Text.StringBuilder(32);
        for (var i = 0; i < 16; i++)
            hex.Append(hash[i].ToString("x2"));
        return hex.ToString();
    }
}

/// <summary>
/// The world's NPC settlements, as its creator's game has them. Client → server: from the world's creator (or the
/// in-game host), taken only while the server has none yet. Server → client: on entering the world, so every game puts
/// its NPC settlements in the same places.
/// </summary>
public sealed class NpcLayout : IPacket
{
    public List<NpcSettlement> Settlements { get; set; } = new();

    public PacketType Type => PacketType.NpcLayout;

    public void Write(ByteWriter writer) => NpcSettlement.WriteList(writer, Settlements);

    public static NpcLayout Read(ByteReader reader) => new() { Settlements = NpcSettlement.ReadList(reader) };
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

/// <summary>
/// Co-op traffic between the host (who simulates) and its guests. Guest → server: always to the host,
/// <see cref="PlayerId"/> ignored. Host → server: to <see cref="PlayerId"/>, or every guest when -1.
/// Server → client: <see cref="PlayerId"/> is the sender.
/// </summary>
public sealed class CoopMessage : IPacket
{
    public int PlayerId { get; set; } = -1;
    public Coop.CoopChannel Channel { get; set; }
    public byte[] Data { get; set; } = System.Array.Empty<byte>();

    public PacketType Type => PacketType.CoopMessage;

    public void Write(ByteWriter writer)
    {
        writer.WriteVarInt(PlayerId);
        writer.WriteByte((byte)Channel);
        writer.WriteBytes(Data);
    }

    public static CoopMessage Read(ByteReader reader) => new()
    {
        PlayerId = (int)reader.ReadVarInt(),
        Channel = (Coop.CoopChannel)reader.ReadByte(),
        Data = reader.ReadBytes(),
    };
}

/// <summary>Client → server: a diplomatic step towards another player (by owner key).</summary>
public sealed class DiplomacyRequest : IPacket
{
    public ulong Target { get; set; }
    public DiplomacyAction Action { get; set; }

    /// <summary>For pacts, peace with a tribute and ultimatums.</summary>
    public TreatyTerms Terms { get; set; } = TreatyTerms.None;

    public PacketType Type => PacketType.DiplomacyRequest;

    public void Write(ByteWriter writer)
    {
        writer.WriteUInt64(Target);
        writer.WriteByte((byte)Action);
        Terms.Write(writer);
    }

    public static DiplomacyRequest Read(ByteReader reader)
    {
        var request = new DiplomacyRequest
        {
            Target = reader.ReadUInt64(),
            Action = (DiplomacyAction)reader.ReadByte(),
            Terms = TreatyTerms.Read(reader),
        };
        if (request.Action < DiplomacyAction.DeclareWar || request.Action > DiplomacyAction.DemandTribute)
            throw new ProtocolException($"Unknown diplomacy action {(byte)request.Action}");
        return request;
    }
}

/// <summary>Server → client: something happened between two players (a proposal to answer, a war, a peace…).</summary>
public sealed class DiplomacyNotice : IPacket
{
    public ulong From { get; set; }
    public string FromName { get; set; } = "";
    public ulong To { get; set; }
    public string ToName { get; set; } = "";
    public DiplomacyEvent Event { get; set; }

    /// <summary>What was proposed or agreed (days, tribute).</summary>
    public TreatyTerms Terms { get; set; } = TreatyTerms.None;

    /// <summary>The treaty concerned (a tribute due, a treaty made), or 0.</summary>
    public long TreatyId { get; set; }

    public PacketType Type => PacketType.DiplomacyNotice;

    public void Write(ByteWriter writer)
    {
        writer.WriteUInt64(From);
        writer.WriteString(FromName);
        writer.WriteUInt64(To);
        writer.WriteString(ToName);
        writer.WriteByte((byte)Event);
        Terms.Write(writer);
        writer.WriteVarInt(TreatyId);
    }

    public static DiplomacyNotice Read(ByteReader reader)
    {
        var notice = new DiplomacyNotice
        {
            From = reader.ReadUInt64(),
            FromName = reader.ReadRequiredString(),
            To = reader.ReadUInt64(),
            ToName = reader.ReadRequiredString(),
            Event = (DiplomacyEvent)reader.ReadByte(),
            Terms = TreatyTerms.Read(reader),
            TreatyId = reader.ReadVarInt(),
        };
        if (notice.Event < DiplomacyEvent.WarDeclared || notice.Event > DiplomacyEvent.TributePaid)
            throw new ProtocolException($"Unknown diplomacy event {(byte)notice.Event}");
        return notice;
    }
}
