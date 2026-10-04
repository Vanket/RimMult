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

    /// <summary>
    /// Host → guests: where the pawns are (a <see cref="PositionsFrame"/>), many times a second. Sent unreliably:
    /// a lost frame is simply replaced by the next one, and big state never holds it up.
    /// </summary>
    Positions = 7,

    /// <summary>Guest → host: these things (ids) didn't load here, send them again in full.</summary>
    Resync = 8,

    /// <summary>
    /// Host → guest: the gizmo this guest pressed (its <see cref="CoopCommand"/> sent back) opens a window or starts
    /// aiming; it runs in the guest's copy, and what the guest picks there comes back as an order or an edit.
    /// </summary>
    RunLocally = 9,
}

/// <summary>A pawn aiming at something (the warm-up before a shot): guests draw the aim pie and, when selected, the line.</summary>
public readonly struct AimMark
{
    public AimMark(int mapId, int shooterId, int targetX, int targetZ, int degrees)
    {
        MapId = mapId;
        ShooterId = shooterId;
        TargetX = targetX;
        TargetZ = targetZ;
        Degrees = degrees;
    }

    public int MapId { get; }
    public int ShooterId { get; }

    /// <summary>Where the target is drawn, in hundredths of a cell.</summary>
    public int TargetX { get; }
    public int TargetZ { get; }

    /// <summary>Width of the aim pie (shrinks as the shot comes).</summary>
    public int Degrees { get; }
}

/// <summary>A work progress bar (doctoring, building, mining…): guests draw it where the host does.</summary>
public readonly struct ProgressMark
{
    public ProgressMark(int mapId, int x, int z, byte percent)
    {
        MapId = mapId;
        X = x;
        Z = z;
        Percent = percent;
    }

    public int MapId { get; }

    /// <summary>Bar center in hundredths of a cell.</summary>
    public int X { get; }
    public int Z { get; }
    public byte Percent { get; }
}

/// <summary>
/// The small, frequently changing state of a thing (hit points, stack size, plant growth, construction progress,
/// forbidden), applied to the guest's copy in place instead of reloading the whole thing.
/// </summary>
public readonly struct ThingPatch
{
    public const byte NoForbid = 2;

    public ThingPatch(int thingId, int hitPoints, int stackCount, float growth, float workDone, byte forbidden)
    {
        ThingId = thingId;
        HitPoints = hitPoints;
        StackCount = stackCount;
        Growth = growth;
        WorkDone = workDone;
        Forbidden = forbidden;
    }

    public int ThingId { get; }
    public int HitPoints { get; }
    public int StackCount { get; }

    /// <summary>Plant growth 0..1, or -1 for a thing that isn't a plant.</summary>
    public float Growth { get; }

    /// <summary>Construction work done on a frame, or -1.</summary>
    public float WorkDone { get; }

    /// <summary>0/1 forbidden, or <see cref="NoForbid"/> for a thing that can't be forbidden.</summary>
    public byte Forbidden { get; }

    public void Write(ByteWriter writer)
    {
        writer.WriteVarInt(ThingId);
        writer.WriteVarInt(HitPoints);
        writer.WriteVarInt(StackCount);
        writer.WriteFloat(Growth);
        writer.WriteFloat(WorkDone);
        writer.WriteByte(Forbidden);
    }

    public static ThingPatch Read(ByteReader reader) =>
        new((int)reader.ReadVarInt(), (int)reader.ReadVarInt(), (int)reader.ReadVarInt(), reader.ReadFloat(), reader.ReadFloat(), reader.ReadByte());
}

/// <summary>Pawn positions on the guests' maps, in frames small enough for one unreliable packet.</summary>
public sealed class PositionsFrame
{
    /// <summary>The host's game tick, so the guest's clock follows between state batches too.</summary>
    public int Tick { get; set; }

    public List<(int MapId, List<PawnPosition> Positions)> Maps { get; set; } = new();

    /// <summary>
    /// Whether this frame carries the aim and progress marks (the first frame of a send does): the guest replaces
    /// its marks only then, the other frames of the same send leave them alone.
    /// </summary>
    public bool HasMarks { get; set; }

    public List<AimMark> Aims { get; set; } = new();
    public List<ProgressMark> Bars { get; set; } = new();

    public byte[] Encode()
    {
        var writer = new ByteWriter(256);
        writer.WriteVarInt(Tick);
        writer.WriteBool(HasMarks);
        if (HasMarks)
        {
            writer.WriteVarUInt((ulong)Aims.Count);
            foreach (var a in Aims)
            {
                writer.WriteVarInt(a.MapId);
                writer.WriteVarInt(a.ShooterId);
                writer.WriteVarInt(a.TargetX);
                writer.WriteVarInt(a.TargetZ);
                writer.WriteVarInt(a.Degrees);
            }
            writer.WriteVarUInt((ulong)Bars.Count);
            foreach (var b in Bars)
            {
                writer.WriteVarInt(b.MapId);
                writer.WriteVarInt(b.X);
                writer.WriteVarInt(b.Z);
                writer.WriteByte(b.Percent);
            }
        }
        writer.WriteVarUInt((ulong)Maps.Count);
        foreach (var (mapId, positions) in Maps)
        {
            writer.WriteVarInt(mapId);
            writer.WriteVarUInt((ulong)positions.Count);
            foreach (var p in positions)
                p.Write(writer);
        }
        return writer.ToArray();
    }

    public static PositionsFrame Decode(byte[] data)
    {
        var reader = new ByteReader(data);
        var frame = new PositionsFrame { Tick = (int)reader.ReadVarInt(), HasMarks = reader.ReadBool() };
        if (frame.HasMarks)
        {
            var aims = MapDelta.Count(reader, 1000);
            for (var i = 0; i < aims; i++)
                frame.Aims.Add(new AimMark((int)reader.ReadVarInt(), (int)reader.ReadVarInt(), (int)reader.ReadVarInt(), (int)reader.ReadVarInt(), (int)reader.ReadVarInt()));
            var bars = MapDelta.Count(reader, 1000);
            for (var i = 0; i < bars; i++)
                frame.Bars.Add(new ProgressMark((int)reader.ReadVarInt(), (int)reader.ReadVarInt(), (int)reader.ReadVarInt(), reader.ReadByte()));
        }
        var maps = MapDelta.Count(reader, 1000);
        for (var i = 0; i < maps; i++)
        {
            var mapId = (int)reader.ReadVarInt();
            var count = MapDelta.Count(reader, 100_000);
            var positions = new List<PawnPosition>(count);
            for (var j = 0; j < count; j++)
                positions.Add(PawnPosition.Read(reader));
            frame.Maps.Add((mapId, positions));
        }
        reader.EnsureFullyRead();
        return frame;
    }

    /// <summary>
    /// Splits positions into frames of at most about <paramref name="maxBytes"/> each (an unreliable packet must
    /// stay small); every frame stands on its own.
    /// </summary>
    public static List<byte[]> Split(int tick, IEnumerable<(int MapId, List<PawnPosition> Positions)> maps, int maxBytes,
        List<AimMark>? aims = null, List<ProgressMark>? bars = null)
    {
        // The marks ride in a frame of their own: a handful of entries, never worth splitting.
        var frames = new List<byte[]>
        {
            new PositionsFrame { Tick = tick, HasMarks = true, Aims = aims ?? new List<AimMark>(), Bars = bars ?? new List<ProgressMark>() }.Encode(),
        };
        // Worst case per entry: id (5) + cell (2×3) + rotation (1) + draw offsets (2×3); map header ~10 bytes.
        const int entryBytes = 18;
        var perFrame = System.Math.Max(1, (maxBytes - 24) / entryBytes);
        var current = new PositionsFrame { Tick = tick };
        var inCurrent = 0;
        foreach (var (mapId, positions) in maps)
        {
            var index = 0;
            do
            {
                var take = System.Math.Min(perFrame - inCurrent, positions.Count - index);
                current.Maps.Add((mapId, positions.GetRange(index, take)));
                index += take;
                inCurrent += take;
                if (inCurrent >= perFrame)
                {
                    frames.Add(current.Encode());
                    current = new PositionsFrame { Tick = tick };
                    inCurrent = 0;
                }
            }
            while (index < positions.Count);
        }
        if (current.Maps.Count > 0)
            frames.Add(current.Encode());
        return frames;
    }
}

public readonly struct PawnPosition
{
    public PawnPosition(int thingId, int x, int z, byte rotation)
        : this(thingId, x, z, rotation, x * 100 + 50, z * 100 + 50)
    {
    }

    public PawnPosition(int thingId, int x, int z, byte rotation, int drawX, int drawZ)
    {
        ThingId = thingId;
        X = x;
        Z = z;
        Rotation = rotation;
        DrawX = drawX;
        DrawZ = drawZ;
    }

    public int ThingId { get; }

    /// <summary>The cell the pawn stands on.</summary>
    public int X { get; }
    public int Z { get; }
    public byte Rotation { get; }

    /// <summary>Where the host draws the pawn, in hundredths of a cell (between cells while walking).</summary>
    public int DrawX { get; }
    public int DrawZ { get; }

    public void Write(ByteWriter writer)
    {
        writer.WriteVarInt(ThingId);
        writer.WriteVarInt(X);
        writer.WriteVarInt(Z);
        writer.WriteByte(Rotation);
        writer.WriteVarInt(DrawX - X * 100);
        writer.WriteVarInt(DrawZ - Z * 100);
    }

    public static PawnPosition Read(ByteReader reader)
    {
        var id = (int)reader.ReadVarInt();
        var x = (int)reader.ReadVarInt();
        var z = (int)reader.ReadVarInt();
        var rotation = reader.ReadByte();
        return new PawnPosition(id, x, z, rotation, x * 100 + (int)reader.ReadVarInt(), z * 100 + (int)reader.ReadVarInt());
    }
}

/// <summary>A projectile the host fired: guests draw it flying (and play the shot) without simulating it.</summary>
public readonly struct CoopShot
{
    public CoopShot(string projectileDef, float fromX, float fromZ, float toX, float toZ, int ticks, string sound)
    {
        ProjectileDef = projectileDef;
        FromX = fromX;
        FromZ = fromZ;
        ToX = toX;
        ToZ = toZ;
        Ticks = ticks;
        Sound = sound;
    }

    public string ProjectileDef { get; }
    public float FromX { get; }
    public float FromZ { get; }
    public float ToX { get; }
    public float ToZ { get; }

    /// <summary>Flight time in game ticks.</summary>
    public int Ticks { get; }

    /// <summary>The weapon's firing sound def, or "".</summary>
    public string Sound { get; }

    public void Write(ByteWriter writer)
    {
        writer.WriteString(ProjectileDef);
        writer.WriteFloat(FromX);
        writer.WriteFloat(FromZ);
        writer.WriteFloat(ToX);
        writer.WriteFloat(ToZ);
        writer.WriteVarInt(Ticks);
        writer.WriteString(Sound);
    }

    public static CoopShot Read(ByteReader reader) => new(
        reader.ReadRequiredString(), reader.ReadFloat(), reader.ReadFloat(), reader.ReadFloat(), reader.ReadFloat(),
        (int)reader.ReadVarInt(), reader.ReadRequiredString());
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

    /// <summary>Small changes to things the guest already has, applied in place.</summary>
    public List<ThingPatch> Patches { get; set; } = new();

    /// <summary>Shots fired since the last batch (drawn by guests, never simulated there).</summary>
    public List<CoopShot> Shots { get; set; } = new();

    /// <summary>The full designation list of the map, when it changed; null otherwise.</summary>
    public List<DesignationEntry>? Designations { get; set; }

    /// <summary>Terrain/roof/fog grids as Scribe XML, when one changed; null otherwise.</summary>
    public string? Grids { get; set; }

    /// <summary>The map's zones (stockpiles, growing zones, …) as Scribe XML, when they changed; null otherwise.</summary>
    public string? Zones { get; set; }

    /// <summary>The map's areas (home, allowed areas, …) as Scribe XML, when they changed; null otherwise.</summary>
    public string? Areas { get; set; }

    public void Write(ByteWriter writer)
    {
        writer.WriteVarInt(MapId);
        writer.WriteVarUInt((ulong)Positions.Count);
        foreach (var p in Positions)
            p.Write(writer);
        writer.WriteVarUInt((ulong)Despawned.Count);
        foreach (var id in Despawned)
            writer.WriteVarInt(id);
        writer.WriteVarUInt((ulong)Things.Count);
        foreach (var thing in Things)
            writer.WriteString(thing);
        writer.WriteVarUInt((ulong)Patches.Count);
        foreach (var patch in Patches)
            patch.Write(writer);
        writer.WriteVarUInt((ulong)Shots.Count);
        foreach (var shot in Shots)
            shot.Write(writer);
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
        writer.WriteString(Areas);
    }

    public static MapDelta Read(ByteReader reader)
    {
        const int maxEntries = 1_000_000;
        var delta = new MapDelta { MapId = (int)reader.ReadVarInt() };
        var positions = Count(reader, maxEntries);
        for (var i = 0; i < positions; i++)
            delta.Positions.Add(PawnPosition.Read(reader));
        var despawned = Count(reader, maxEntries);
        for (var i = 0; i < despawned; i++)
            delta.Despawned.Add((int)reader.ReadVarInt());
        var things = Count(reader, maxEntries);
        for (var i = 0; i < things; i++)
            delta.Things.Add(reader.ReadRequiredString());
        var patches = Count(reader, maxEntries);
        for (var i = 0; i < patches; i++)
            delta.Patches.Add(ThingPatch.Read(reader));
        var shots = Count(reader, 10_000);
        for (var i = 0; i < shots; i++)
            delta.Shots.Add(CoopShot.Read(reader));
        if (reader.ReadBool())
        {
            var designations = Count(reader, maxEntries);
            delta.Designations = new List<DesignationEntry>(designations);
            for (var i = 0; i < designations; i++)
                delta.Designations.Add(new DesignationEntry(reader.ReadRequiredString(), (int)reader.ReadVarInt(), (int)reader.ReadVarInt(), (int)reader.ReadVarInt()));
        }
        delta.Grids = reader.ReadString();
        delta.Zones = reader.ReadString();
        delta.Areas = reader.ReadString();
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

/// <summary>A letter the host received (an event, a raid, a quest offer): guests get the same letter.</summary>
public sealed class CoopLetter
{
    public string Label { get; set; } = "";
    public string Text { get; set; } = "";
    public string Def { get; set; } = "";

    /// <summary>Where the letter points: a cell on a map, or -1 for none.</summary>
    public int MapId { get; set; } = -1;
    public int X { get; set; }
    public int Z { get; set; }

    public void Write(ByteWriter writer)
    {
        writer.WriteString(Label);
        writer.WriteString(Text);
        writer.WriteString(Def);
        writer.WriteVarInt(MapId);
        writer.WriteVarInt(X);
        writer.WriteVarInt(Z);
    }

    public static CoopLetter Read(ByteReader reader) => new()
    {
        Label = reader.ReadRequiredString(),
        Text = reader.ReadRequiredString(),
        Def = reader.ReadRequiredString(),
        MapId = (int)reader.ReadVarInt(),
        X = (int)reader.ReadVarInt(),
        Z = (int)reader.ReadVarInt(),
    };
}

/// <summary>The colony's relation with one NPC faction (by load id), as the host has it.</summary>
public readonly struct FactionStanding
{
    public FactionStanding(string factionId, int goodwill, byte kind)
    {
        FactionId = factionId;
        Goodwill = goodwill;
        Kind = kind;
    }

    public string FactionId { get; }
    public int Goodwill { get; }

    /// <summary>RimWorld's FactionRelationKind (hostile / neutral / ally).</summary>
    public byte Kind { get; }
}

/// <summary>One NPC-world part of the shared game in co-op: relations, research, letters.</summary>
public sealed class CoopWorld
{
    /// <summary>All faction relations, when one changed; null otherwise.</summary>
    public List<FactionStanding>? Factions { get; set; }

    /// <summary>Research progress that changed (project def → points); null when nothing did.</summary>
    public List<(string Project, float Progress)>? Research { get; set; }

    /// <summary>The current research project ("" for none), when it changed; null otherwise.</summary>
    public string? CurrentResearch { get; set; }

    public List<CoopLetter> Letters { get; set; } = new();

    /// <summary>The colony's apparel, drug and food policies as Scribe XML, when they changed; null otherwise.</summary>
    public string? Policies { get; set; }

    public bool IsEmpty => Factions == null && Research == null && CurrentResearch == null && Letters.Count == 0 && Policies == null;

    public void Write(ByteWriter writer)
    {
        writer.WriteBool(Factions != null);
        if (Factions != null)
        {
            writer.WriteVarUInt((ulong)Factions.Count);
            foreach (var f in Factions)
            {
                writer.WriteString(f.FactionId);
                writer.WriteVarInt(f.Goodwill);
                writer.WriteByte(f.Kind);
            }
        }
        writer.WriteBool(Research != null);
        if (Research != null)
        {
            writer.WriteVarUInt((ulong)Research.Count);
            foreach (var (project, progress) in Research)
            {
                writer.WriteString(project);
                writer.WriteFloat(progress);
            }
        }
        writer.WriteString(CurrentResearch);
        writer.WriteVarUInt((ulong)Letters.Count);
        foreach (var letter in Letters)
            letter.Write(writer);
        writer.WriteString(Policies);
    }

    public static CoopWorld Read(ByteReader reader)
    {
        var world = new CoopWorld();
        if (reader.ReadBool())
        {
            var count = MapDelta.Count(reader, 10_000);
            world.Factions = new List<FactionStanding>(count);
            for (var i = 0; i < count; i++)
                world.Factions.Add(new FactionStanding(reader.ReadRequiredString(), (int)reader.ReadVarInt(), reader.ReadByte()));
        }
        if (reader.ReadBool())
        {
            var count = MapDelta.Count(reader, 100_000);
            world.Research = new List<(string, float)>(count);
            for (var i = 0; i < count; i++)
                world.Research.Add((reader.ReadRequiredString(), reader.ReadFloat()));
        }
        world.CurrentResearch = reader.ReadString();
        var letters = MapDelta.Count(reader, 1000);
        for (var i = 0; i < letters; i++)
            world.Letters.Add(CoopLetter.Read(reader));
        world.Policies = reader.ReadString();
        return world;
    }
}

/// <summary>One frame of co-op state from the host: a delta per map, plus the world-wide parts.</summary>
public sealed class CoopBatch
{
    /// <summary>The host's game tick (TicksGame), so the guest's clock and date follow the host's.</summary>
    public int Tick { get; set; }

    public List<MapDelta> Maps { get; set; } = new();

    public CoopWorld World { get; set; } = new();

    public byte[] Encode()
    {
        var writer = new ByteWriter(4096);
        writer.WriteVarInt(Tick);
        writer.WriteVarUInt((ulong)Maps.Count);
        foreach (var map in Maps)
            map.Write(writer);
        World.Write(writer);
        return writer.ToArray();
    }

    public static CoopBatch Decode(byte[] data)
    {
        var reader = new ByteReader(data);
        var batch = new CoopBatch { Tick = (int)reader.ReadVarInt() };
        var count = MapDelta.Count(reader, 1000);
        for (var i = 0; i < count; i++)
            batch.Maps.Add(MapDelta.Read(reader));
        batch.World = CoopWorld.Read(reader);
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

    /// <summary>A targeting gizmo (attack, cast an ability, rescue, …) aimed at a thing or a cell.</summary>
    Target = 6,

    /// <summary>
    /// Settings the guest changed in its copy (bills, storage, plants, owners, a pawn's schedule and policies, the
    /// policies themselves, areas), sent as their saved state for the host to take over.
    /// </summary>
    Edit = 7,
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

    /// <summary>The order was given with the queue key held (Shift): it goes after the pawn's current orders.</summary>
    public bool Queue { get; set; }

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
        writer.WriteBool(Queue);
        return writer.ToArray();
    }

    public static CoopCommand Decode(byte[] data)
    {
        var reader = new ByteReader(data);
        var command = new CoopCommand { Kind = (CoopCommandKind)reader.ReadByte(), MapId = (int)reader.ReadVarInt() };
        if (command.Kind < CoopCommandKind.Designate || command.Kind > CoopCommandKind.Edit)
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
        command.Queue = reader.ReadBool();
        reader.EnsureFullyRead();
        return command;
    }
}

/// <summary>A plain list of thing ids (resync requests).</summary>
public static class CoopIds
{
    public static byte[] Encode(IReadOnlyCollection<int> ids)
    {
        var writer = new ByteWriter();
        writer.WriteVarUInt((ulong)ids.Count);
        foreach (var id in ids)
            writer.WriteVarInt(id);
        return writer.ToArray();
    }

    public static List<int> Decode(byte[] data)
    {
        var reader = new ByteReader(data);
        var count = MapDelta.Count(reader, 100_000);
        var ids = new List<int>(count);
        for (var i = 0; i < count; i++)
            ids.Add((int)reader.ReadVarInt());
        reader.EnsureFullyRead();
        return ids;
    }
}
