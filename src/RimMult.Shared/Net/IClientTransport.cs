using System;

namespace RimMult.Shared.Net;

/// <summary>
/// Client end of a connection: Steam P2P, UDP or the in-process loopback used by the host's own client.
/// Events are raised only from inside <see cref="Poll"/>, so all handlers run on the caller's (main) thread.
/// </summary>
public interface IClientTransport
{
    event Action? Connected;

    event Action<byte[]>? Received;

    /// <summary>Raised once, after which the transport is dead. The argument is a human-readable reason.</summary>
    event Action<string>? Disconnected;

    /// <summary>Begins connecting; <see cref="Connected"/> or <see cref="Disconnected"/> follows from a later <see cref="Poll"/>.</summary>
    void Start();

    void Send(byte[] data, DeliveryMode mode);

    void Poll();

    /// <summary>Closes the connection without raising <see cref="Disconnected"/>.</summary>
    void Close();
}
