using RimMult.Shared.Serialization;

namespace RimMult.Shared.Packets;

/// <summary>Wire ids. Never renumber an existing entry; append new ones.</summary>
public enum PacketType : ushort
{
    ClientHello = 1,
    ServerWelcome = 2,
    Kick = 3,
    PlayerList = 4,
    Chat = 10,
    SpeedVote = 20,
    AuthorityReport = 21,
    TickGrant = 22,
}

/// <summary>A message body. <see cref="PacketCodec"/> adds the type header.</summary>
public interface IPacket
{
    PacketType Type { get; }

    void Write(ByteWriter writer);
}
