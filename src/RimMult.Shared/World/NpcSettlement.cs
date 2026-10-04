using System.Collections.Generic;
using RimMult.Shared.Serialization;

namespace RimMult.Shared.World;

/// <summary>
/// One NPC settlement of the shared world as the world's creator has it. Every player's game puts its NPC settlements
/// where these are (each colony keeps its own relations with the factions; the map of the world is the same).
/// </summary>
public sealed class NpcSettlement
{
    public const int MaxCount = 20_000;

    /// <summary>World tile in RimWorld's <c>PlanetTile</c> string form.</summary>
    public string Tile { get; set; } = "";

    /// <summary>The world object's def (a mod's settlement may have its own).</summary>
    public string Def { get; set; } = "";

    public string FactionDef { get; set; } = "";

    /// <summary>Which of the factions of that def (by creation order): worlds may have several of the same def.</summary>
    public int FactionIndex { get; set; }

    public string Name { get; set; } = "";

    public void Write(ByteWriter writer)
    {
        writer.WriteString(Tile);
        writer.WriteString(Def);
        writer.WriteString(FactionDef);
        writer.WriteVarInt(FactionIndex);
        writer.WriteString(Name);
    }

    public static NpcSettlement Read(ByteReader reader) => new()
    {
        Tile = reader.ReadRequiredString(),
        Def = reader.ReadRequiredString(),
        FactionDef = reader.ReadRequiredString(),
        FactionIndex = (int)reader.ReadVarInt(),
        Name = reader.ReadRequiredString(),
    };

    public static void WriteList(ByteWriter writer, IReadOnlyList<NpcSettlement> settlements)
    {
        writer.WriteVarUInt((ulong)settlements.Count);
        foreach (var settlement in settlements)
            settlement.Write(writer);
    }

    public static List<NpcSettlement> ReadList(ByteReader reader)
    {
        var count = reader.ReadVarUInt();
        if (count > MaxCount)
            throw new ProtocolException($"Too many NPC settlements: {count}");
        var settlements = new List<NpcSettlement>((int)count);
        for (var i = 0UL; i < count; i++)
            settlements.Add(Read(reader));
        return settlements;
    }
}
