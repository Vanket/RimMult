using RimMult.Shared.Net;

namespace RimMult.ServerCore;

/// <summary>
/// What <see cref="GameServer"/> needs from the network. In practice this is a <see cref="TransportHub"/>,
/// which reports traffic back by calling <see cref="GameServer.OnConnected"/>, <see cref="GameServer.OnDisconnected"/>
/// and <see cref="GameServer.OnData"/> on the server's thread.
/// </summary>
public interface IServerTransport
{
    void Send(int connectionId, byte[] data, DeliveryMode mode);

    /// <summary>
    /// Closes the connection. <paramref name="farewell"/>, if given, is delivered to the client before the close
    /// (used for kick reasons). The transport must still call <see cref="GameServer.OnDisconnected"/>.
    /// </summary>
    void Disconnect(int connectionId, byte[]? farewell = null);
}

/// <summary>
/// One way of accepting connections (Steam P2P, UDP, loopback). Endpoints register their connections with a
/// <see cref="TransportHub"/>, which hands out the ids <see cref="GameServer"/> sees.
/// </summary>
public interface IServerEndpoint
{
    /// <summary>Processes network events; called from <see cref="TransportHub.Poll"/>.</summary>
    void Poll();

    void Send(int connectionId, byte[] data, DeliveryMode mode);

    /// <summary>
    /// Delivers <paramref name="farewell"/> (if any) as the last packet, closes the connection and reports it via
    /// <see cref="TransportHub.ConnectionClosed"/>.
    /// </summary>
    void Disconnect(int connectionId, byte[]? farewell = null);

    /// <summary>Stops accepting and drops every connection.</summary>
    void Stop();
}
