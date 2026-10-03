using System.Collections.Generic;
using System.Linq;
using RimMult.Shared.Serialization;

namespace RimMult.Shared.World;

public enum PlayerRelation : byte
{
    Neutral = 0,
    Allied = 1,

    /// <summary>At war: raids between the two are allowed (if the server allows PvP at all).</summary>
    Hostile = 2,
}

public enum DiplomacyAction : byte
{
    DeclareWar = 1,
    ProposePeace = 2,
    ProposeAlliance = 3,
    BreakAlliance = 4,

    /// <summary>Accept the other player's pending proposal.</summary>
    Accept = 5,

    /// <summary>Turn the other player's pending proposal down.</summary>
    Decline = 6,
}

public enum DiplomacyEvent : byte
{
    WarDeclared = 1,
    PeaceProposed = 2,
    AllianceProposed = 3,
    PeaceMade = 4,
    AllianceMade = 5,
    AllianceBroken = 6,
    ProposalDeclined = 7,
}

/// <summary>The relation between two players (owner keys), stored once per pair with <see cref="A"/> &lt; <see cref="B"/>.</summary>
public sealed class RelationEntry
{
    public RelationEntry(ulong a, ulong b, PlayerRelation relation)
    {
        A = a < b ? a : b;
        B = a < b ? b : a;
        Relation = relation;
    }

    public ulong A { get; }
    public ulong B { get; }
    public PlayerRelation Relation { get; set; }

    public bool Involves(ulong owner) => A == owner || B == owner;

    public bool Is(ulong x, ulong y) => (A == x && B == y) || (A == y && B == x);

    public void Write(ByteWriter writer)
    {
        writer.WriteUInt64(A);
        writer.WriteUInt64(B);
        writer.WriteByte((byte)Relation);
    }

    public static RelationEntry Read(ByteReader reader)
    {
        var a = reader.ReadUInt64();
        var b = reader.ReadUInt64();
        var relation = reader.ReadByte();
        if (relation > (byte)PlayerRelation.Hostile)
            throw new ProtocolException($"Unknown relation {relation}");
        return new RelationEntry(a, b, (PlayerRelation)relation);
    }

    public static void WriteList(ByteWriter writer, IReadOnlyList<RelationEntry> relations)
    {
        writer.WriteVarUInt((ulong)relations.Count);
        foreach (var relation in relations)
            relation.Write(writer);
    }

    public static List<RelationEntry> ReadList(ByteReader reader)
    {
        var count = reader.ReadVarUInt();
        if (count > 100_000)
            throw new ProtocolException($"Too many relations: {count}");
        var relations = new List<RelationEntry>((int)count);
        for (var i = 0UL; i < count; i++)
            relations.Add(Read(reader));
        return relations;
    }

    /// <summary>Relation between two players in a list; neutral when the pair isn't listed.</summary>
    public static PlayerRelation Between(IEnumerable<RelationEntry> relations, ulong x, ulong y) =>
        relations.FirstOrDefault(r => r.Is(x, y))?.Relation ?? PlayerRelation.Neutral;
}

/// <summary>
/// The rules of player diplomacy. War is declared by one side; peace and alliances need both. Relations are
/// persistent (kept in <see cref="WorldState.Relations"/>), proposals only while the server runs.
/// </summary>
public sealed class DiplomacyBook
{
    private readonly List<RelationEntry> _relations;
    private readonly HashSet<(ulong From, ulong To, DiplomacyEvent Kind)> _proposals = new();

    public DiplomacyBook(List<RelationEntry> relations)
    {
        _relations = relations;
    }

    public PlayerRelation Get(ulong x, ulong y) => RelationEntry.Between(_relations, x, y);

    public bool HasProposal(ulong from, ulong to, DiplomacyEvent kind) => _proposals.Contains((from, to, kind));

    /// <summary>
    /// Applies <paramref name="action"/> by <paramref name="from"/> towards <paramref name="to"/>. Returns what
    /// happened (to announce), or null if the action isn't possible right now.
    /// </summary>
    public DiplomacyEvent? Apply(ulong from, ulong to, DiplomacyAction action, bool allowPvp)
    {
        if (from == to)
            return null;
        var current = Get(from, to);

        switch (action)
        {
            case DiplomacyAction.DeclareWar when allowPvp && current == PlayerRelation.Neutral:
                Set(from, to, PlayerRelation.Hostile);
                return DiplomacyEvent.WarDeclared;

            case DiplomacyAction.ProposePeace when current == PlayerRelation.Hostile:
                return Propose(from, to, DiplomacyEvent.PeaceProposed);

            case DiplomacyAction.ProposeAlliance when current == PlayerRelation.Neutral:
                return Propose(from, to, DiplomacyEvent.AllianceProposed);

            case DiplomacyAction.BreakAlliance when current == PlayerRelation.Allied:
                Set(from, to, PlayerRelation.Neutral);
                return DiplomacyEvent.AllianceBroken;

            case DiplomacyAction.Accept:
                // "to" made the proposal; "from" accepts it.
                if (_proposals.Remove((to, from, DiplomacyEvent.PeaceProposed)) && current == PlayerRelation.Hostile)
                {
                    Set(from, to, PlayerRelation.Neutral);
                    return DiplomacyEvent.PeaceMade;
                }
                if (_proposals.Remove((to, from, DiplomacyEvent.AllianceProposed)) && current == PlayerRelation.Neutral)
                {
                    Set(from, to, PlayerRelation.Allied);
                    return DiplomacyEvent.AllianceMade;
                }
                return null;

            case DiplomacyAction.Decline:
                var declined = _proposals.Remove((to, from, DiplomacyEvent.PeaceProposed))
                               | _proposals.Remove((to, from, DiplomacyEvent.AllianceProposed));
                return declined ? DiplomacyEvent.ProposalDeclined : null;

            default:
                return null;
        }
    }

    /// <summary>Proposals made by or to a player who left can't be answered anymore.</summary>
    public void ForgetProposals(ulong owner) => _proposals.RemoveWhere(p => p.From == owner || p.To == owner);

    private DiplomacyEvent? Propose(ulong from, ulong to, DiplomacyEvent kind)
    {
        // Asked twice: say nothing new.
        return _proposals.Add((from, to, kind)) ? kind : null;
    }

    private void Set(ulong x, ulong y, PlayerRelation relation)
    {
        _relations.RemoveAll(r => r.Is(x, y));
        if (relation != PlayerRelation.Neutral)
            _relations.Add(new RelationEntry(x, y, relation));
        // A changed relation makes older proposals between the two meaningless.
        _proposals.RemoveWhere(p => (p.From == x && p.To == y) || (p.From == y && p.To == x));
    }
}
