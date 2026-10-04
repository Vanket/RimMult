using RimMult.Shared.Mods;
using RimMult.Shared.Packets;
using RimMult.Shared.Serialization;
using RimMult.Shared.Time;

namespace RimMult.Tests;

public class SerializationTests
{
    [Theory]
    [InlineData(0L)]
    [InlineData(1L)]
    [InlineData(-1L)]
    [InlineData(127L)]
    [InlineData(128L)]
    [InlineData(long.MaxValue)]
    [InlineData(long.MinValue)]
    public void VarIntRoundTrips(long value)
    {
        var writer = new ByteWriter();
        writer.WriteVarInt(value);
        var reader = new ByteReader(writer.ToArray());
        Assert.Equal(value, reader.ReadVarInt());
        reader.EnsureFullyRead();
    }

    [Fact]
    public void SmallVarIntsAreOneByte()
    {
        var writer = new ByteWriter();
        writer.WriteVarInt(-5);
        Assert.Equal(1, writer.Length);
    }

    [Fact]
    public void PrimitivesRoundTrip()
    {
        var writer = new ByteWriter(1);
        writer.WriteBool(true);
        writer.WriteUInt16(65000);
        writer.WriteInt32(-123456789);
        writer.WriteInt64(long.MinValue + 7);
        writer.WriteUInt64(ulong.MaxValue);
        writer.WriteFloat(-3.25f);
        writer.WriteString(null);
        writer.WriteString("");
        writer.WriteString("Привет, колония");
        writer.WriteBytes([1, 2, 3]);

        var reader = new ByteReader(writer.ToArray());
        Assert.True(reader.ReadBool());
        Assert.Equal(65000, reader.ReadUInt16());
        Assert.Equal(-123456789, reader.ReadInt32());
        Assert.Equal(long.MinValue + 7, reader.ReadInt64());
        Assert.Equal(ulong.MaxValue, reader.ReadUInt64());
        Assert.Equal(-3.25f, reader.ReadFloat());
        Assert.Null(reader.ReadString());
        Assert.Equal("", reader.ReadString());
        Assert.Equal("Привет, колония", reader.ReadString());
        Assert.Equal([1, 2, 3], reader.ReadBytes());
        reader.EnsureFullyRead();
    }

    [Fact]
    public void TruncatedDataThrowsProtocolException()
    {
        var writer = new ByteWriter();
        writer.WriteString("hello");
        var data = writer.ToArray();
        var reader = new ByteReader(data, 0, data.Length - 1);
        Assert.Throws<ProtocolException>(() => reader.ReadString());
    }

    [Fact]
    public void HugeLengthPrefixIsRejectedBeforeAllocating()
    {
        var writer = new ByteWriter();
        writer.WriteVarUInt(ulong.MaxValue);
        Assert.Throws<ProtocolException>(() => new ByteReader(writer.ToArray()).ReadBytes());
    }

    [Fact]
    public void ClientHelloRoundTrips()
    {
        var hello = new ClientHello
        {
            SteamId = 76561198000000001,
            DisplayName = "Vanket",
            GameVersion = "1.6.4633 rev1261",
            Mods = [new ModEntry("ludeon.rimworld", "Core", "1.6", 0), new ModEntry("ceteam.combatextended", "Combat Extended", "abc", 2890901044)],
            Password = null,
        };

        var decoded = Assert.IsType<ClientHello>(PacketCodec.Decode(PacketCodec.Encode(hello)));
        Assert.Equal(hello.ProtocolVersion, decoded.ProtocolVersion);
        Assert.Equal(hello.SteamId, decoded.SteamId);
        Assert.Equal(hello.DisplayName, decoded.DisplayName);
        Assert.Equal(hello.GameVersion, decoded.GameVersion);
        Assert.Equal(2, decoded.Mods.Count);
        Assert.Equal("Combat Extended", decoded.Mods[1].Name);
        Assert.Equal(2890901044UL, decoded.Mods[1].WorkshopId);
        Assert.Equal(ModListHash.Compute(hello.Mods), ModListHash.Compute(decoded.Mods));
        Assert.Null(decoded.Password);
    }

    [Fact]
    public void WelcomeCarriesTimeSettings()
    {
        var welcome = new ServerWelcome
        {
            PlayerId = 3,
            ServerName = "Test",
            IsHost = true,
            Time = new TimeSettings { VoteMode = SpeedVoteMode.Majority, AnyoneCanPause = false, MaxSpeed = GameSpeed.Fast, MaxDriftTicks = 120 },
        };

        var decoded = Assert.IsType<ServerWelcome>(PacketCodec.Decode(PacketCodec.Encode(welcome)));
        Assert.Equal(3, decoded.PlayerId);
        Assert.True(decoded.IsHost);
        Assert.Equal(SpeedVoteMode.Majority, decoded.Time.VoteMode);
        Assert.False(decoded.Time.AnyoneCanPause);
        Assert.Equal(GameSpeed.Fast, decoded.Time.MaxSpeed);
        Assert.Equal(120, decoded.Time.MaxDriftTicks);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(GameSpeed.Paused)]
    [InlineData(GameSpeed.Superfast)]
    public void SpeedVoteRoundTrips(GameSpeed? speed)
    {
        var decoded = Assert.IsType<SpeedVote>(PacketCodec.Decode(PacketCodec.Encode(new SpeedVote { Speed = speed })));
        Assert.Equal(speed, decoded.Speed);
    }

    [Fact]
    public void TickGrantRoundTrips()
    {
        var grant = new TickGrant { HorizonTick = 1_234_567, Speed = GameSpeed.Fast, BottleneckPlayerId = -1 };
        var decoded = Assert.IsType<TickGrant>(PacketCodec.Decode(PacketCodec.Encode(grant)));
        Assert.Equal(grant.HorizonTick, decoded.HorizonTick);
        Assert.Equal(grant.Speed, decoded.Speed);
        Assert.Equal(-1, decoded.BottleneckPlayerId);
        Assert.False(decoded.Boost);
        Assert.True(Assert.IsType<TickGrant>(PacketCodec.Decode(PacketCodec.Encode(new TickGrant { Speed = GameSpeed.Superfast, Boost = true }))).Boost);

        var report = Assert.IsType<AuthorityReport>(PacketCodec.Decode(PacketCodec.Encode(new AuthorityReport { Tick = 9, SustainableTicksPerSecond = 700, Idle = true })));
        Assert.Equal((9L, 700f, true), (report.Tick, report.SustainableTicksPerSecond, report.Idle));
    }

    [Fact]
    public void UnknownPacketTypeIsRejected()
    {
        Assert.Throws<ProtocolException>(() => PacketCodec.Decode([0xFF, 0xFF]));
    }

    [Fact]
    public void TrailingBytesAreRejected()
    {
        var data = PacketCodec.Encode(new SpeedVote { Speed = GameSpeed.Normal }).Append((byte)0).ToArray();
        Assert.Throws<ProtocolException>(() => PacketCodec.Decode(data));
    }

    [Fact]
    public void KickRoundTripsWithAndWithoutModList()
    {
        var plain = Assert.IsType<Kick>(PacketCodec.Decode(PacketCodec.Encode(new Kick { Reason = KickReason.ServerFull, Message = "full" })));
        Assert.Equal(KickReason.ServerFull, plain.Reason);
        Assert.Null(plain.ServerMods);

        var withMods = new Kick { Reason = KickReason.ModListMismatch, Message = "mods", ServerMods = [new ModEntry("a.b", "AB", "1", 7)] };
        var decoded = Assert.IsType<Kick>(PacketCodec.Decode(PacketCodec.Encode(withMods)));
        Assert.Equal("a.b", Assert.Single(decoded.ServerMods!).PackageId);
    }
}
