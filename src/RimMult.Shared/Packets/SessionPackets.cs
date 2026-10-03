using System.Collections.Generic;
using RimMult.Shared.Serialization;
using RimMult.Shared.Time;

namespace RimMult.Shared.Packets;

/// <summary>First packet from a client. Everything needed to accept or reject it.</summary>
public sealed class ClientHello : IPacket
{
    public int ProtocolVersion { get; set; } = ProtocolInfo.Version;
    public ulong SteamId { get; set; }
    public string DisplayName { get; set; } = "";
    public string GameVersion { get; set; } = "";
    public string ModListHash { get; set; } = "";
    public string? Password { get; set; }

    public PacketType Type => PacketType.ClientHello;

    public void Write(ByteWriter writer)
    {
        // Protocol version first, so any future layout can still be rejected cleanly.
        writer.WriteVarInt(ProtocolVersion);
        writer.WriteUInt64(SteamId);
        writer.WriteString(DisplayName);
        writer.WriteString(GameVersion);
        writer.WriteString(ModListHash);
        writer.WriteString(Password);
    }

    public static ClientHello Read(ByteReader reader) => new()
    {
        ProtocolVersion = (int)reader.ReadVarInt(),
        SteamId = reader.ReadUInt64(),
        DisplayName = reader.ReadRequiredString(),
        GameVersion = reader.ReadRequiredString(),
        ModListHash = reader.ReadRequiredString(),
        Password = reader.ReadString(),
    };
}

public sealed class ServerWelcome : IPacket
{
    public int PlayerId { get; set; }
    public string ServerName { get; set; } = "";
    public bool IsHost { get; set; }
    public TimeSettings Time { get; set; } = new();

    public PacketType Type => PacketType.ServerWelcome;

    public void Write(ByteWriter writer)
    {
        writer.WriteVarInt(PlayerId);
        writer.WriteString(ServerName);
        writer.WriteBool(IsHost);
        Time.Write(writer);
    }

    public static ServerWelcome Read(ByteReader reader) => new()
    {
        PlayerId = (int)reader.ReadVarInt(),
        ServerName = reader.ReadRequiredString(),
        IsHost = reader.ReadBool(),
        Time = TimeSettings.Read(reader),
    };
}

public enum KickReason : byte
{
    Unspecified = 0,
    ProtocolMismatch = 1,
    GameVersionMismatch = 2,
    ModListMismatch = 3,
    WrongPassword = 4,
    ServerFull = 5,
    AlreadyConnected = 6,
    Kicked = 7,
    ServerShutdown = 8,
    BadData = 9,
}

/// <summary>Sent right before the server drops a connection, so the client can show why.</summary>
public sealed class Kick : IPacket
{
    public KickReason Reason { get; set; }
    public string Message { get; set; } = "";

    public PacketType Type => PacketType.Kick;

    public void Write(ByteWriter writer)
    {
        writer.WriteByte((byte)Reason);
        writer.WriteString(Message);
    }

    public static Kick Read(ByteReader reader) => new()
    {
        Reason = (KickReason)reader.ReadByte(),
        Message = reader.ReadRequiredString(),
    };
}

public sealed class PlayerInfo
{
    public int Id { get; set; }
    public ulong SteamId { get; set; }
    public string Name { get; set; } = "";
    public bool IsHost { get; set; }

    public void Write(ByteWriter writer)
    {
        writer.WriteVarInt(Id);
        writer.WriteUInt64(SteamId);
        writer.WriteString(Name);
        writer.WriteBool(IsHost);
    }

    public static PlayerInfo Read(ByteReader reader) => new()
    {
        Id = (int)reader.ReadVarInt(),
        SteamId = reader.ReadUInt64(),
        Name = reader.ReadRequiredString(),
        IsHost = reader.ReadBool(),
    };
}

/// <summary>Full roster, broadcast whenever someone joins or leaves. Small enough at 10 players not to bother with deltas.</summary>
public sealed class PlayerList : IPacket
{
    public const int MaxPlayers = 255;

    public List<PlayerInfo> Players { get; set; } = new();

    public PacketType Type => PacketType.PlayerList;

    public void Write(ByteWriter writer)
    {
        writer.WriteVarUInt((ulong)Players.Count);
        foreach (var player in Players)
            player.Write(writer);
    }

    public static PlayerList Read(ByteReader reader)
    {
        var count = reader.ReadVarUInt();
        if (count > MaxPlayers)
            throw new ProtocolException($"Player list too long: {count}");

        var list = new PlayerList();
        for (var i = 0UL; i < count; i++)
            list.Players.Add(PlayerInfo.Read(reader));
        return list;
    }
}

public sealed class ChatMessage : IPacket
{
    public const int MaxLength = 500;

    /// <summary>Filled in by the server; whatever the client sends here is ignored.</summary>
    public int SenderId { get; set; }
    public string Text { get; set; } = "";

    public PacketType Type => PacketType.Chat;

    public void Write(ByteWriter writer)
    {
        writer.WriteVarInt(SenderId);
        writer.WriteString(Text);
    }

    public static ChatMessage Read(ByteReader reader) => new()
    {
        SenderId = (int)reader.ReadVarInt(),
        Text = reader.ReadRequiredString(),
    };
}
