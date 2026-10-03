using System;
using System.Collections.Generic;
using RimMult.Shared.Net;

namespace RimMult.ServerCore;

/// <summary>
/// Lets one <see cref="GameServer"/> accept players over several endpoints at once
/// (the host in-game: loopback for itself + Steam P2P for friends + optional UDP port).
/// </summary>
public sealed class TransportHub : IServerTransport
{
    private readonly List<IServerEndpoint> _endpoints = new();
    private readonly Dictionary<int, IServerEndpoint> _owners = new();
    private GameServer? _server;
    private int _nextConnectionId = 1;

    public void Attach(GameServer server) => _server = server;

    public void AddEndpoint(IServerEndpoint endpoint) => _endpoints.Add(endpoint);

    /// <summary>Called by an endpoint when a new peer connects. Returns the id to use for it from now on.</summary>
    public int RegisterConnection(IServerEndpoint owner)
    {
        var id = _nextConnectionId++;
        _owners[id] = owner;
        Server.OnConnected(id);
        return id;
    }

    public void Receive(int connectionId, byte[] data)
    {
        if (_owners.ContainsKey(connectionId))
            Server.OnData(connectionId, data, 0, data.Length);
    }

    /// <summary>Called by an endpoint when a connection is gone, for whatever reason. Safe to call twice.</summary>
    public void ConnectionClosed(int connectionId)
    {
        if (_owners.Remove(connectionId))
            Server.OnDisconnected(connectionId);
    }

    public void Poll()
    {
        foreach (var endpoint in _endpoints)
            endpoint.Poll();
    }

    public void Stop()
    {
        foreach (var endpoint in _endpoints)
            endpoint.Stop();
        _endpoints.Clear();
    }

    public void Send(int connectionId, byte[] data, DeliveryMode mode)
    {
        if (_owners.TryGetValue(connectionId, out var owner))
            owner.Send(connectionId, data, mode);
    }

    public void Disconnect(int connectionId, byte[]? farewell = null)
    {
        if (_owners.TryGetValue(connectionId, out var owner))
            owner.Disconnect(connectionId, farewell);
    }

    private GameServer Server => _server ?? throw new InvalidOperationException("TransportHub is not attached to a GameServer");
}
