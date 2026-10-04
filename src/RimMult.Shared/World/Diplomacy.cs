using System;
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

/// <summary>Wire values: never renumber, append new ones.</summary>
public enum DiplomacyAction : byte
{
    DeclareWar = 1,

    /// <summary>With <see cref="TreatyTerms.Tribute"/>: peace on condition that one side pays the other.</summary>
    ProposePeace = 2,
    ProposeAlliance = 3,
    BreakAlliance = 4,

    /// <summary>Accept the other player's pending proposal.</summary>
    Accept = 5,

    /// <summary>Turn the other player's pending proposal down.</summary>
    Decline = 6,

    /// <summary>A non-aggression pact for <see cref="TreatyTerms.Days"/>: neither side may declare war until it ends.</summary>
    ProposePact = 7,

    /// <summary>Tear up every treaty with the other player (pact, truce, tribute): everyone sees who broke it.</summary>
    BreakTreaty = 8,

    /// <summary>An ultimatum: pay <see cref="TreatyTerms.Tribute"/> silver every quadrum for <see cref="TreatyTerms.Days"/>, or it's war.</summary>
    DemandTribute = 9,
}

/// <summary>Wire values: never renumber, append new ones.</summary>
public enum DiplomacyEvent : byte
{
    WarDeclared = 1,
    PeaceProposed = 2,
    AllianceProposed = 3,
    PeaceMade = 4,
    AllianceMade = 5,
    AllianceBroken = 6,
    ProposalDeclined = 7,
    PactProposed = 8,
    PactMade = 9,

    /// <summary><see cref="DiplomacyNotice.From"/> tore up its treaties with <see cref="DiplomacyNotice.To"/>.</summary>
    TreatyBroken = 10,
    TributeDemanded = 11,

    /// <summary>The ultimatum was accepted: <see cref="DiplomacyNotice.From"/> pays <see cref="DiplomacyNotice.To"/>.</summary>
    TributeAgreed = 12,

    /// <summary>The ultimatum was turned down (<see cref="DiplomacyNotice.From"/> refused): it's war.</summary>
    UltimatumRejected = 13,

    /// <summary>A pact or a tribute between the two ran its course.</summary>
    TreatyExpired = 14,

    /// <summary>To the payer only: a tribute payment is due now (<see cref="DiplomacyNotice.TreatyId"/>).</summary>
    TributeDue = 15,

    /// <summary><see cref="DiplomacyNotice.From"/> paid <see cref="DiplomacyNotice.To"/> its tribute.</summary>
    TributePaid = 16,
}

/// <summary>Wire values: never renumber, append new ones.</summary>
public enum TreatyKind : byte
{
    /// <summary>Non-aggression pact.</summary>
    Pact = 1,

    /// <summary>Right after a peace: no new war for a few days.</summary>
    Truce = 2,

    /// <summary><see cref="Treaty.Payer"/> pays the other side every quadrum; no war meanwhile.</summary>
    Tribute = 3,
}

/// <summary>What a proposal comes with: how long, and how much silver changes hands.</summary>
public sealed class TreatyTerms
{
    public const int MinDays = 1;
    public const int MaxDays = 120;
    public const int MaxTribute = 100_000;

    /// <summary>Game days the treaty lasts.</summary>
    public int Days { get; set; }

    /// <summary>Silver per quadrum (0: none).</summary>
    public int Tribute { get; set; }

    /// <summary>The proposing side pays the tribute (a peace offered with a payment); otherwise it receives it.</summary>
    public bool ProposerPays { get; set; }

    public static TreatyTerms None => new();

    public void Write(ByteWriter writer)
    {
        writer.WriteVarInt(Days);
        writer.WriteVarInt(Tribute);
        writer.WriteBool(ProposerPays);
    }

    public static TreatyTerms Read(ByteReader reader)
    {
        var terms = new TreatyTerms
        {
            Days = (int)reader.ReadVarInt(),
            Tribute = (int)reader.ReadVarInt(),
            ProposerPays = reader.ReadBool(),
        };
        if (terms.Days < 0 || terms.Days > MaxDays || terms.Tribute < 0 || terms.Tribute > MaxTribute)
            throw new ProtocolException($"Bad treaty terms: {terms.Days} days, {terms.Tribute} silver");
        return terms;
    }

    /// <summary>The same terms within the allowed range.</summary>
    public TreatyTerms Clamped() => new()
    {
        Days = Math.Max(MinDays, Math.Min(MaxDays, Days)),
        Tribute = Math.Max(0, Math.Min(MaxTribute, Tribute)),
        ProposerPays = ProposerPays,
    };
}

/// <summary>A treaty between two players (owner keys); while it runs, neither may declare war on the other.</summary>
public sealed class Treaty
{
    public const int TicksPerDay = 60_000;

    /// <summary>A tribute is paid once per quadrum (15 days).</summary>
    public const long TributeIntervalTicks = 15L * TicksPerDay;

    public long Id { get; set; }
    public TreatyKind Kind { get; set; }
    public ulong A { get; set; }
    public ulong B { get; set; }
    public long StartTick { get; set; }
    public long EndTick { get; set; }

    /// <summary>Who pays (<see cref="TreatyKind.Tribute"/>): <see cref="A"/> or <see cref="B"/>.</summary>
    public ulong Payer { get; set; }

    /// <summary>Silver per payment.</summary>
    public int Amount { get; set; }

    /// <summary>When the next payment is due (world ticks).</summary>
    public long NextDueTick { get; set; }

    /// <summary>Payments made so far.</summary>
    public int Payments { get; set; }

    public ulong Receiver => Payer == A ? B : A;

    public bool Involves(ulong owner) => A == owner || B == owner;

    public bool Is(ulong x, ulong y) => (A == x && B == y) || (A == y && B == x);

    public ulong Other(ulong owner) => owner == A ? B : A;

    /// <summary>A payment is due and still within the treaty.</summary>
    public bool PaymentDue(long now) => Kind == TreatyKind.Tribute && now >= NextDueTick && NextDueTick < EndTick;

    public void Write(ByteWriter writer)
    {
        writer.WriteVarInt(Id);
        writer.WriteByte((byte)Kind);
        writer.WriteUInt64(A);
        writer.WriteUInt64(B);
        writer.WriteVarInt(StartTick);
        writer.WriteVarInt(EndTick);
        writer.WriteUInt64(Payer);
        writer.WriteVarInt(Amount);
        writer.WriteVarInt(NextDueTick);
        writer.WriteVarInt(Payments);
    }

    public static Treaty Read(ByteReader reader)
    {
        var treaty = new Treaty
        {
            Id = reader.ReadVarInt(),
            Kind = (TreatyKind)reader.ReadByte(),
            A = reader.ReadUInt64(),
            B = reader.ReadUInt64(),
            StartTick = reader.ReadVarInt(),
            EndTick = reader.ReadVarInt(),
            Payer = reader.ReadUInt64(),
            Amount = (int)reader.ReadVarInt(),
            NextDueTick = reader.ReadVarInt(),
            Payments = (int)reader.ReadVarInt(),
        };
        if (treaty.Kind < TreatyKind.Pact || treaty.Kind > TreatyKind.Tribute)
            throw new ProtocolException($"Unknown treaty {(byte)treaty.Kind}");
        return treaty;
    }

    public static void WriteList(ByteWriter writer, IReadOnlyList<Treaty> treaties)
    {
        writer.WriteVarUInt((ulong)treaties.Count);
        foreach (var treaty in treaties)
            treaty.Write(writer);
    }

    public static List<Treaty> ReadList(ByteReader reader)
    {
        var count = reader.ReadVarUInt();
        if (count > 100_000)
            throw new ProtocolException($"Too many treaties: {count}");
        var treaties = new List<Treaty>((int)count);
        for (var i = 0UL; i < count; i++)
            treaties.Add(Read(reader));
        return treaties;
    }

    /// <summary>Treaties between two players that still run at <paramref name="now"/>.</summary>
    public static IEnumerable<Treaty> Between(IEnumerable<Treaty> treaties, ulong x, ulong y, long now) =>
        treaties.Where(t => t.Is(x, y) && t.EndTick > now);
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
/// The rules of player diplomacy. War is declared by one side; peace, alliances, pacts and tributes need both.
/// Treaties (a pact, the truce after a peace, a tribute) forbid war while they run; breaking one is allowed but
/// public. Relations and treaties are persistent (kept in <see cref="WorldState"/>), proposals only while the server runs.
/// </summary>
public sealed class DiplomacyBook
{
    /// <summary>After a peace, no new war for this many days.</summary>
    public const int TruceDays = 5;

    private readonly List<RelationEntry> _relations;
    private readonly List<Treaty> _treaties;
    private readonly Func<long> _nextTreatyId;
    private readonly Dictionary<(ulong From, ulong To, DiplomacyEvent Kind), TreatyTerms> _proposals = new();
    private long _localIds;

    public DiplomacyBook(List<RelationEntry> relations, List<Treaty>? treaties = null, Func<long>? nextTreatyId = null)
    {
        _relations = relations;
        _treaties = treaties ?? new List<Treaty>();
        _nextTreatyId = nextTreatyId ?? (() => ++_localIds);
    }

    public IReadOnlyList<Treaty> Treaties => _treaties;

    /// <summary>The terms of the proposal (or ultimatum) behind the last event <see cref="Apply"/> returned.</summary>
    public TreatyTerms LastTerms { get; private set; } = TreatyTerms.None;

    /// <summary>The treaty the last event <see cref="Apply"/> returned made (a pact, a truce, a tribute), or null.</summary>
    public Treaty? LastTreaty { get; private set; }

    public PlayerRelation Get(ulong x, ulong y) => RelationEntry.Between(_relations, x, y);

    public bool HasProposal(ulong from, ulong to, DiplomacyEvent kind) => _proposals.ContainsKey((from, to, kind));

    /// <summary>A treaty between the two still runs: no war until it ends (or someone breaks it).</summary>
    public bool Bound(ulong x, ulong y, long now) => Treaty.Between(_treaties, x, y, now).Any();

    /// <summary>
    /// Applies <paramref name="action"/> by <paramref name="from"/> towards <paramref name="to"/> at world time
    /// <paramref name="now"/>. Returns what happened (to announce), or null if the action isn't possible right now.
    /// </summary>
    public DiplomacyEvent? Apply(ulong from, ulong to, DiplomacyAction action, bool allowPvp, long now = 0, TreatyTerms? terms = null)
    {
        LastTerms = TreatyTerms.None;
        LastTreaty = null;
        if (from == to)
            return null;
        var current = Get(from, to);
        var bound = Bound(from, to, now);

        switch (action)
        {
            case DiplomacyAction.DeclareWar when allowPvp && current == PlayerRelation.Neutral && !bound:
                Set(from, to, PlayerRelation.Hostile);
                return DiplomacyEvent.WarDeclared;

            case DiplomacyAction.ProposePeace when current == PlayerRelation.Hostile:
                return Propose(from, to, DiplomacyEvent.PeaceProposed, terms is { Tribute: > 0 } ? terms.Clamped() : TreatyTerms.None);

            case DiplomacyAction.ProposeAlliance when current == PlayerRelation.Neutral:
                return Propose(from, to, DiplomacyEvent.AllianceProposed, TreatyTerms.None);

            case DiplomacyAction.ProposePact when current == PlayerRelation.Neutral && !bound:
                return Propose(from, to, DiplomacyEvent.PactProposed, (terms ?? TreatyTerms.None).Clamped());

            case DiplomacyAction.DemandTribute when allowPvp && current == PlayerRelation.Neutral && !bound && terms is { Tribute: > 0 }:
                var demand = terms.Clamped();
                demand.ProposerPays = false;
                return Propose(from, to, DiplomacyEvent.TributeDemanded, demand);

            case DiplomacyAction.BreakAlliance when current == PlayerRelation.Allied:
                Set(from, to, PlayerRelation.Neutral);
                return DiplomacyEvent.AllianceBroken;

            case DiplomacyAction.BreakTreaty when bound:
                _treaties.RemoveAll(t => t.Is(from, to));
                return DiplomacyEvent.TreatyBroken;

            case DiplomacyAction.Accept:
                return Accept(from, to, current, bound, now);

            case DiplomacyAction.Decline:
                return Decline(from, to, current, bound, allowPvp);

            default:
                return null;
        }
    }

    private DiplomacyEvent? Accept(ulong from, ulong to, PlayerRelation current, bool bound, long now)
    {
        // "to" made the proposal; "from" accepts it.
        if (Take(to, from, DiplomacyEvent.PeaceProposed) is { } peace && current == PlayerRelation.Hostile)
        {
            Set(from, to, PlayerRelation.Neutral);
            LastTerms = peace;
            LastTreaty = peace.Tribute > 0
                ? AddTreaty(TreatyKind.Tribute, from, to, now, peace.Days, peace.ProposerPays ? to : from, peace.Tribute)
                : AddTreaty(TreatyKind.Truce, from, to, now, TruceDays);
            return DiplomacyEvent.PeaceMade;
        }
        if (Take(to, from, DiplomacyEvent.AllianceProposed) is not null && current == PlayerRelation.Neutral)
        {
            Set(from, to, PlayerRelation.Allied);
            return DiplomacyEvent.AllianceMade;
        }
        if (Take(to, from, DiplomacyEvent.PactProposed) is { } pact && current == PlayerRelation.Neutral && !bound)
        {
            LastTerms = pact;
            LastTreaty = AddTreaty(TreatyKind.Pact, from, to, now, pact.Days);
            return DiplomacyEvent.PactMade;
        }
        if (Take(to, from, DiplomacyEvent.TributeDemanded) is { } demand && current == PlayerRelation.Neutral && !bound)
        {
            // The one who accepts the ultimatum pays.
            LastTerms = demand;
            LastTreaty = AddTreaty(TreatyKind.Tribute, from, to, now, demand.Days, from, demand.Tribute);
            return DiplomacyEvent.TributeAgreed;
        }
        return null;
    }

    private DiplomacyEvent? Decline(ulong from, ulong to, PlayerRelation current, bool bound, bool allowPvp)
    {
        // Turning an ultimatum down means war.
        if (Take(to, from, DiplomacyEvent.TributeDemanded) is { } demand)
        {
            LastTerms = demand;
            if (allowPvp && current == PlayerRelation.Neutral && !bound)
            {
                Set(from, to, PlayerRelation.Hostile);
                return DiplomacyEvent.UltimatumRejected;
            }
            return DiplomacyEvent.ProposalDeclined;
        }
        var declined = Take(to, from, DiplomacyEvent.PeaceProposed) != null
                       | Take(to, from, DiplomacyEvent.AllianceProposed) != null
                       | Take(to, from, DiplomacyEvent.PactProposed) != null;
        return declined ? DiplomacyEvent.ProposalDeclined : null;
    }

    /// <summary>Treaties that ran their course by <paramref name="now"/>; they are removed.</summary>
    public List<Treaty> Expire(long now)
    {
        var over = _treaties.Where(t => t.EndTick <= now).ToList();
        if (over.Count > 0)
            _treaties.RemoveAll(t => t.EndTick <= now);
        return over;
    }

    /// <summary>A tribute payment arrived: the next one is due a quadrum later. Null if it doesn't settle a payment.</summary>
    public Treaty? Paid(long treatyId, ulong payer, int amount)
    {
        var treaty = _treaties.FirstOrDefault(t => t.Id == treatyId && t.Kind == TreatyKind.Tribute && t.Payer == payer);
        if (treaty == null || amount < treaty.Amount)
            return null;
        treaty.Payments++;
        treaty.NextDueTick += Treaty.TributeIntervalTicks;
        return treaty;
    }

    /// <summary>Proposals made by or to a player who left can't be answered anymore.</summary>
    public void ForgetProposals(ulong owner)
    {
        foreach (var key in _proposals.Keys.Where(p => p.From == owner || p.To == owner).ToList())
            _proposals.Remove(key);
    }

    private DiplomacyEvent? Propose(ulong from, ulong to, DiplomacyEvent kind, TreatyTerms terms)
    {
        // Asked twice: say nothing new.
        if (_proposals.ContainsKey((from, to, kind)))
            return null;
        _proposals[(from, to, kind)] = terms;
        LastTerms = terms;
        return kind;
    }

    private TreatyTerms? Take(ulong from, ulong to, DiplomacyEvent kind)
    {
        if (!_proposals.TryGetValue((from, to, kind), out var terms))
            return null;
        _proposals.Remove((from, to, kind));
        return terms;
    }

    private Treaty AddTreaty(TreatyKind kind, ulong x, ulong y, long now, int days, ulong payer = 0, int amount = 0)
    {
        var treaty = new Treaty
        {
            Id = _nextTreatyId(),
            Kind = kind,
            A = x,
            B = y,
            StartTick = now,
            EndTick = now + (long)Math.Max(1, days) * Treaty.TicksPerDay,
            Payer = payer,
            Amount = amount,
            NextDueTick = now, // the first payment right away
        };
        _treaties.Add(treaty);
        return treaty;
    }

    private void Set(ulong x, ulong y, PlayerRelation relation)
    {
        _relations.RemoveAll(r => r.Is(x, y));
        if (relation != PlayerRelation.Neutral)
            _relations.Add(new RelationEntry(x, y, relation));
        // A changed relation makes older proposals between the two meaningless, and ends their treaties (a new peace
        // brings its own truce).
        foreach (var key in _proposals.Keys.Where(p => (p.From == x && p.To == y) || (p.From == y && p.To == x)).ToList())
            _proposals.Remove(key);
        _treaties.RemoveAll(t => t.Is(x, y));
    }
}
