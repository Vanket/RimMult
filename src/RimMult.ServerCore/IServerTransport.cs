namespace RimMult.ServerCore;

public enum DeliveryMode
{
    /// <summary>Commands, events, anything that must arrive exactly once and in order.</summary>
    ReliableOrdered,

    /// <summary>State that the next packet supersedes (tick grants, pawn positions). Drops and stale packets are discarded.</summary>
    UnreliableSequenced,
}

/// <summary>
/// What <see cref="GameServer"/> needs from the network: Steam sockets in-game, LiteNetLib in the dedicated server.
/// The transport reports traffic back by calling <see cref="GameServer.OnConnected"/>,
/// <see cref="GameServer.OnDisconnected"/> and <see cref="GameServer.OnData"/> on the server's thread.
/// </summary>
public interface IServerTransport
{
    void Send(int connectionId, byte[] data, DeliveryMode mode);

    /// <summary>Closes the connection. The transport must still call <see cref="GameServer.OnDisconnected"/>.</summary>
    void Disconnect(int connectionId);
}
