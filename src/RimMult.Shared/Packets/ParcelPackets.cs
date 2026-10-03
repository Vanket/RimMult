using RimMult.Shared.Serialization;
using RimMult.Shared.World;

namespace RimMult.Shared.Packets;

/// <summary>Client → server: send items to another player's colony.</summary>
public sealed class ParcelSend : IPacket
{
    public ulong ToOwner { get; set; }
    public string ToTile { get; set; } = "";
    public string Summary { get; set; } = "";
    public byte[] Payload { get; set; } = System.Array.Empty<byte>();

    public PacketType Type => PacketType.ParcelSend;

    public void Write(ByteWriter writer)
    {
        writer.WriteUInt64(ToOwner);
        writer.WriteString(ToTile);
        writer.WriteString(Summary);
        writer.WriteBytes(Payload);
    }

    public static ParcelSend Read(ByteReader reader) => new()
    {
        ToOwner = reader.ReadUInt64(),
        ToTile = reader.ReadRequiredString(),
        Summary = reader.ReadRequiredString(),
        Payload = reader.ReadBytes(),
    };
}

/// <summary>Server → client: a parcel for you. Re-sent on every entry into the world until acknowledged.</summary>
public sealed class ParcelDeliver : IPacket
{
    public MailItem Item { get; set; } = new();

    public PacketType Type => PacketType.ParcelDeliver;

    public void Write(ByteWriter writer) => Item.Write(writer);

    public static ParcelDeliver Read(ByteReader reader) => new() { Item = MailItem.Read(reader) };
}

/// <summary>Client → server: parcel received (or already received earlier); the server may forget it.</summary>
public sealed class ParcelAck : IPacket
{
    public long Id { get; set; }

    public PacketType Type => PacketType.ParcelAck;

    public void Write(ByteWriter writer) => writer.WriteVarInt(Id);

    public static ParcelAck Read(ByteReader reader) => new() { Id = reader.ReadVarInt() };
}
