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
    WorldCreate = 30,
    WorldUpdate = 31,
    EnterWorld = 32,
    LeaveWorld = 33,
    WorldClock = 34,
    MyColonies = 35,
    NpcLayout = 36,
    ParcelSend = 40,
    ParcelDeliver = 41,
    ParcelAck = 42,
    SettlementDestroyed = 43,
    PlayerRelay = 50,
    CoopMessage = 60,
    DiplomacyRequest = 70,
    DiplomacyNotice = 71,
    ChronicleUpdate = 80,
    ColonyStatsReport = 81,
    ModListQuery = 90,
    ServerModList = 91,
    MarketAction = 100,
    MarketState = 101,
    AdminAction = 110,
}

/// <summary>A message body. <see cref="PacketCodec"/> adds the type header.</summary>
public interface IPacket
{
    PacketType Type { get; }

    void Write(ByteWriter writer);
}
