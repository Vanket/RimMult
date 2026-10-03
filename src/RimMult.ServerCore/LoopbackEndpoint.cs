using System;
using System.Collections.Generic;
using RimMult.Shared.Net;

namespace RimMult.ServerCore;

/// <summary>
/// In-process connections: the hosting player's own client talks to the server through the same packets as
/// everyone else, just without a network. Everything is queued and delivered on the next poll, never re-entrantly.
/// </summary>
public sealed class LoopbackEndpoint : IServerEndpoint
{
    private readonly TransportHub _hub;
    private readonly Dictionary<int, Client> _clients = new();
    private readonly Queue<Action> _inbound = new();

    public LoopbackEndpoint(TransportHub hub)
    {
        _hub = hub;
    }

    public IClientTransport CreateClient() => new Client(this);

    public void Poll()
    {
        // Handlers may enqueue more work (a reply), which is then handled on the next poll.
        var count = _inbound.Count;
        for (var i = 0; i < count; i++)
            _inbound.Dequeue()();
    }

    public void Send(int connectionId, byte[] data, DeliveryMode mode)
    {
        if (_clients.TryGetValue(connectionId, out var client))
            client.Enqueue(() => client.RaiseReceived(data));
    }

    public void Disconnect(int connectionId, byte[]? farewell = null)
    {
        if (!_clients.TryGetValue(connectionId, out var client))
            return;

        _clients.Remove(connectionId);
        if (farewell != null)
            client.Enqueue(() => client.RaiseReceived(farewell));
        client.Enqueue(() => client.RaiseDisconnected("Disconnected by server"));
        _hub.ConnectionClosed(connectionId);
    }

    public void Stop()
    {
        foreach (var id in new List<int>(_clients.Keys))
            Disconnect(id);
        _inbound.Clear();
    }

    private sealed class Client : IClientTransport
    {
        private readonly LoopbackEndpoint _endpoint;
        private readonly Queue<Action> _events = new();
        private int _connectionId = -1;
        private bool _closed;

        public Client(LoopbackEndpoint endpoint)
        {
            _endpoint = endpoint;
        }

        public event Action? Connected;
        public event Action<byte[]>? Received;
        public event Action<string>? Disconnected;

        public void Start()
        {
            _endpoint._inbound.Enqueue(() =>
            {
                if (_closed)
                    return;
                _connectionId = _endpoint._hub.RegisterConnection(_endpoint);
                _endpoint._clients[_connectionId] = this;
                Enqueue(() => Connected?.Invoke());
            });
        }

        public void Send(byte[] data, DeliveryMode mode)
        {
            _endpoint._inbound.Enqueue(() =>
            {
                if (!_closed && _connectionId >= 0)
                    _endpoint._hub.Receive(_connectionId, data);
            });
        }

        public void Poll()
        {
            while (_events.Count > 0 && !_closed)
                _events.Dequeue()();
        }

        public void Close()
        {
            if (_closed)
                return;
            _closed = true;
            _events.Clear();
            var id = _connectionId;
            _endpoint._inbound.Enqueue(() =>
            {
                if (id >= 0 && _endpoint._clients.Remove(id))
                    _endpoint._hub.ConnectionClosed(id);
            });
        }

        internal void Enqueue(Action action) => _events.Enqueue(action);

        internal void RaiseReceived(byte[] data) => Received?.Invoke(data);

        internal void RaiseDisconnected(string reason)
        {
            _closed = true;
            Disconnected?.Invoke(reason);
        }
    }
}
