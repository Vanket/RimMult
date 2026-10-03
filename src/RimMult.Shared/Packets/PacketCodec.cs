using RimMult.Shared.Serialization;

namespace RimMult.Shared.Packets;

public static class PacketCodec
{
    public static byte[] Encode(IPacket packet)
    {
        var writer = new ByteWriter();
        writer.WriteUInt16((ushort)packet.Type);
        packet.Write(writer);
        return writer.ToArray();
    }

    public static IPacket Decode(byte[] data) => Decode(data, 0, data.Length);

    public static IPacket Decode(byte[] data, int offset, int count)
    {
        var reader = new ByteReader(data, offset, count);
        var type = (PacketType)reader.ReadUInt16();
        IPacket packet = type switch
        {
            PacketType.ClientHello => ClientHello.Read(reader),
            PacketType.ServerWelcome => ServerWelcome.Read(reader),
            PacketType.Kick => Kick.Read(reader),
            PacketType.PlayerList => PlayerList.Read(reader),
            PacketType.Chat => ChatMessage.Read(reader),
            PacketType.SpeedVote => SpeedVote.Read(reader),
            PacketType.AuthorityReport => AuthorityReport.Read(reader),
            PacketType.TickGrant => TickGrant.Read(reader),
            _ => throw new ProtocolException($"Unknown packet type {(ushort)type}"),
        };
        reader.EnsureFullyRead();
        return packet;
    }
}
