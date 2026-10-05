using RimMult.Shared.Serialization;
using RimMult.Shared.World;

namespace RimMult.Shared.Packets;

/// <summary>Wire values: never renumber, append new ones.</summary>
public enum AdminActionKind : byte
{
    /// <summary>Items (packed in <see cref="AdminAction.Payload"/> by the admin's game) for <see cref="AdminAction.Target"/>.</summary>
    GiveItems = 1,

    /// <summary>Research projects finished for <see cref="AdminAction.Target"/>: def names, one per line, in the payload.</summary>
    GiveResearch = 2,

    /// <summary>Take lot <see cref="AdminAction.Id"/> off the market (a player's goods go back to them).</summary>
    RemoveLot = 3,

    /// <summary>Call order <see cref="AdminAction.Id"/> off (a player's reward goes back to them).</summary>
    RemoveOrder = 4,

    /// <summary>All prices back to normal, news dropped.</summary>
    ResetPrices = 5,

    /// <summary>Market news now.</summary>
    MarketNews = 6,

    /// <summary><see cref="AdminAction.Target"/> and <see cref="AdminAction.Other"/> are now <see cref="AdminAction.Relation"/> (treaties end).</summary>
    SetRelation = 7,

    /// <summary>Every treaty between <see cref="AdminAction.Target"/> and <see cref="AdminAction.Other"/> ends.</summary>
    BreakTreaties = 8,

    /// <summary>The world stays paused for everyone until <see cref="ResumeWorld"/>.</summary>
    PauseWorld = 9,
    ResumeWorld = 10,
}

/// <summary>Client → server: something only the host or an admin may do (the server checks).</summary>
public sealed class AdminAction : IPacket
{
    public AdminActionKind Kind { get; set; }

    /// <summary>The player concerned (owner key).</summary>
    public ulong Target { get; set; }

    /// <summary>The other player of a relation (owner key).</summary>
    public ulong Other { get; set; }

    /// <summary>The lot or order concerned.</summary>
    public long Id { get; set; }

    public PlayerRelation Relation { get; set; }

    /// <summary>What a gift is ("Steel x500"), for the recipient's letter.</summary>
    public string Summary { get; set; } = "";

    public byte[] Payload { get; set; } = System.Array.Empty<byte>();

    public PacketType Type => PacketType.AdminAction;

    public void Write(ByteWriter writer)
    {
        writer.WriteByte((byte)Kind);
        writer.WriteUInt64(Target);
        writer.WriteUInt64(Other);
        writer.WriteVarInt(Id);
        writer.WriteByte((byte)Relation);
        writer.WriteString(Summary);
        writer.WriteBytes(Payload);
    }

    public static AdminAction Read(ByteReader reader)
    {
        var action = new AdminAction
        {
            Kind = (AdminActionKind)reader.ReadByte(),
            Target = reader.ReadUInt64(),
            Other = reader.ReadUInt64(),
            Id = reader.ReadVarInt(),
            Relation = (PlayerRelation)reader.ReadByte(),
            Summary = reader.ReadRequiredString(),
            Payload = reader.ReadBytes(),
        };
        if (action.Kind < AdminActionKind.GiveItems || action.Kind > AdminActionKind.ResumeWorld)
            throw new ProtocolException($"Unknown admin action {(byte)action.Kind}");
        if (action.Relation > PlayerRelation.Hostile)
            throw new ProtocolException($"Unknown relation {(byte)action.Relation}");
        if (action.Payload.Length > MailItem.MaxPayloadBytes)
            throw new ProtocolException("Admin gift too large");
        return action;
    }
}
