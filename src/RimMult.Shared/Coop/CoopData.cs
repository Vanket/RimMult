using System.Collections.Generic;
using RimMult.Shared.Serialization;

namespace RimMult.Shared.Coop;

public enum GameMode : byte
{
    /// <summary>Every player has their own colony on the shared planet; each machine simulates only its own.</summary>
    SeparateColonies = 0,

    /// <summary>Everyone plays the host's colony: the host simulates, guests see a live copy and give orders.</summary>
    Coop = 1,
}

public enum CoopChannel : byte
{
    /// <summary>Guest → host: send me the game.</summary>
    JoinRequest = 1,

    /// <summary>Host → guest: the whole game (a save file, gzipped).</summary>
    Game = 2,

    /// <summary>Guest → host: loaded, start streaming. Also releases the server's pause hold.</summary>
    Ready = 3,

    /// <summary>Host → guests: a <see cref="CoopBatch"/> of changes.</summary>
    State = 4,

    /// <summary>Guest → host: a <see cref="CoopCommand"/> to carry out.</summary>
    Command = 5,

    /// <summary>Host → guest: there is no game to join yet (host in the main menu).</summary>
    NotReady = 6,
}

public readonly struct PawnPosition
{
    public PawnPosition(int thingId, int x, int z, byte rotation)
    {
        ThingId = thingId;
        X = x;
        Z = z;
        Rotation = rotation;
    }

    public int ThingId { get; }
    public int X { get; }
    public int Z { get; }
    public byte Rotation { get; }
}

public readonly struct DesignationEntry
{
    public DesignationEntry(string defName, int thingId, int x, int z)
    {
        DefName = defName;
        ThingId = thingId;
        X = x;
        Z = z;
    }

    public string DefName { get; }

    /// <summary>Target thing, or -1 for a cell designation at (<see cref="X"/>, <see cref="Z"/>).</summary>
    public int ThingId { get; }
    public int X { get; }
    public int Z { get; }
}

/// <summary>What changed on one map since the last batch.</summary>
public sealed class MapDelta
{
    public int MapId { get; set; }
    public List<PawnPosition> Positions { get; set; } = new();
    public List<int> Despawned { get; set; } = new();

    /// <summary>Things spawned or changed: each a standalone Scribe XML fragment (opaque here).</summary>
    public List<string> Things { get; set; } = new();

    /// <summary>The full designation list of the map, when it changed; null otherwise.</summary>
    public List<DesignationEntry>? Designations { get; set; }

    /// <summary>Terrain/roof/fog grids as Scribe XML, when one changed; null otherwise.</summary>
    public string? Grids { get; set; }

    /// <summary>The map's zones (stockpiles, growing zones, …) as Scribe XML, when they changed; null otherwise.</summary>
    public string? Zones { get; set; }

    public void Write(ByteWriter writer)
    {
        writer.WriteVarInt(MapId);
        writer.WriteVarUInt((ulong)Positions.Count);
        foreach (var p in Positions)
        {
            writer.WriteVarInt(p.ThingId);
            writer.WriteVarInt(p.X);
            writer.WriteVarInt(p.Z);
            writer.WriteByte(p.Rotation);
        }
        writer.WriteVarUInt((ulong)Despawned.Count);
        foreach (var id in Despawned)
            writer.WriteVarInt(id);
        writer.WriteVarUInt((ulong)Things.Count);
        foreach (var thing in Things)
            writer.WriteString(thing);
        writer.WriteBool(Designations != null);
        if (Designations != null)
        {
            writer.WriteVarUInt((ulong)Designations.Count);
            foreach (var d in Designations)
            {
                writer.WriteString(d.DefName);
                writer.WriteVarInt(d.ThingId);
                writer.WriteVarInt(d.X);
                writer.WriteVarInt(d.Z);
            }
        }
        writer.WriteString(Grids);
        writer.WriteString(Zones);
    }

    public static MapDelta Read(ByteReader reader)
    {
        const int maxEntries = 1_000_000;
        var delta = new MapDelta { MapId = (int)reader.ReadVarInt() };
        var positions = Count(reader, maxEntries);
        for (var i = 0; i < positions; i++)
            delta.Positions.Add(new PawnPosition((int)reader.ReadVarInt(), (int)reader.ReadVarInt(), (int)reader.ReadVarInt(), reader.ReadByte()));
        var despawned = Count(reader, maxEntries);
        for (var i = 0; i < despawned; i++)
            delta.Despawned.Add((int)reader.ReadVarInt());
        var things = Count(reader, maxEntries);
        for (var i = 0; i < things; i++)
            delta.Things.Add(reader.ReadRequiredString());
        if (reader.ReadBool())
        {
            var designations = Count(reader, maxEntries);
            delta.Designations = new List<DesignationEntry>(designations);
            for (var i = 0; i < designations; i++)
                delta.Designations.Add(new DesignationEntry(reader.ReadRequiredString(), (int)reader.ReadVarInt(), (int)reader.ReadVarInt(), (int)reader.ReadVarInt()));
        }
        delta.Grids = reader.ReadString();
        delta.Zones = reader.ReadString();
        return delta;
    }

    internal static int Count(ByteReader reader, int max)
    {
        var count = reader.ReadVarUInt();
        if (count > (ulong)max)
            throw new ProtocolException($"Too many entries: {count}");
        return (int)count;
    }
}

/// <summary>One frame of co-op state from the host: a delta per map.</summary>
public sealed class CoopBatch
{
    /// <summary>The host's game tick (TicksGame), so the guest's clock and date follow the host's.</summary>
    public int Tick { get; set; }

    public List<MapDelta> Maps { get; set; } = new();

    public byte[] Encode()
    {
        var writer = new ByteWriter(4096);
        writer.WriteVarInt(Tick);
        writer.WriteVarUInt((ulong)Maps.Count);
        foreach (var map in Maps)
            map.Write(writer);
        return writer.ToArray();
    }

    public static CoopBatch Decode(byte[] data)
    {
        var reader = new ByteReader(data);
        var batch = new CoopBatch { Tick = (int)reader.ReadVarInt() };
        var count = MapDelta.Count(reader, 1000);
        for (var i = 0; i < count; i++)
            batch.Maps.Add(MapDelta.Read(reader));
        reader.EnsureFullyRead();
        return batch;
    }
}

public enum CoopCommandKind : byte
{
    /// <summary>A designator (mine, chop, build, zone, …) used on cells or a thing.</summary>
    Designate = 1,

    /// <summary>A right-click menu option, picked by label for the selected pawns at a map position.</summary>
    FloatMenu = 2,

    /// <summary>A gizmo button (draft, forbid, …), picked by type and label on the selected things.</summary>
    Gizmo = 3,

    /// <summary>A work priority change.</summary>
    WorkPriority = 4,

    /// <summary>The current research project.</summary>
    Research = 5,
}

/// <summary>An order a co-op guest gave; the host finds the same designator/option/gizmo in its game and runs it.</summary>
public sealed class CoopCommand
{
    public CoopCommandKind Kind { get; set; }
    public int MapId { get; set; }
    public List<int> ThingIds { get; set; } = new();
    public List<int> Cells { get; set; } = new(); // x, z pairs

    /// <summary>Designator/command class, option label, work type or research def — depending on <see cref="Kind"/>.</summary>
    public string Name { get; set; } = "";

    /// <summary>Def of the thing to build, label of a gizmo, …</summary>
    public string Detail { get; set; } = "";

    /// <summary>Stuff of the thing to build.</summary>
    public string Extra { get; set; } = "";

    /// <summary>Rotation, priority, … depending on <see cref="Kind"/>.</summary>
    public int Number { get; set; }

    public float X { get; set; }
    public float Z { get; set; }

    public byte[] Encode()
    {
        var writer = new ByteWriter();
        writer.WriteByte((byte)Kind);
        writer.WriteVarInt(MapId);
        writer.WriteVarUInt((ulong)ThingIds.Count);
        foreach (var id in ThingIds)
            writer.WriteVarInt(id);
        writer.WriteVarUInt((ulong)Cells.Count);
        foreach (var c in Cells)
            writer.WriteVarInt(c);
        writer.WriteString(Name);
        writer.WriteString(Detail);
        writer.WriteString(Extra);
        writer.WriteVarInt(Number);
        writer.WriteFloat(X);
        writer.WriteFloat(Z);
        return writer.ToArray();
    }

    public static CoopCommand Decode(byte[] data)
    {
        var reader = new ByteReader(data);
        var command = new CoopCommand { Kind = (CoopCommandKind)reader.ReadByte(), MapId = (int)reader.ReadVarInt() };
        if (command.Kind < CoopCommandKind.Designate || command.Kind > CoopCommandKind.Research)
            throw new ProtocolException($"Unknown co-op command {(byte)command.Kind}");
        var things = MapDelta.Count(reader, 100_000);
        for (var i = 0; i < things; i++)
            command.ThingIds.Add((int)reader.ReadVarInt());
        var cells = MapDelta.Count(reader, 2_000_000);
        if (cells % 2 != 0)
            throw new ProtocolException("Odd cell list");
        for (var i = 0; i < cells; i++)
            command.Cells.Add((int)reader.ReadVarInt());
        command.Name = reader.ReadRequiredString();
        command.Detail = reader.ReadRequiredString();
        command.Extra = reader.ReadRequiredString();
        command.Number = (int)reader.ReadVarInt();
        command.X = reader.ReadFloat();
        command.Z = reader.ReadFloat();
        reader.EnsureFullyRead();
        return command;
    }
}
