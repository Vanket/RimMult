using System;
using System.Collections.Generic;
using LiteNetLib;
using RimMult.ServerCore;
using RimMult.Shared;
using RimMult.Shared.Net;

namespace RimMult.Net.LiteNet;

/// <summary>Accepts players over UDP. All callbacks fire inside <see cref="Poll"/>, on the caller's thread.</summary>
public sealed class LiteNetServerEndpoint : IServerEndpoint
{
    private readonly TransportHub _hub;
    private readonly NetManager _net;
    private readonly Dictionary<int, int> _connectionByPeer = new();
    private readonly Dictionary<int, NetPeer> _peerByConnection = new();

    public LiteNetServerEndpoint(TransportHub hub, int maxConnections)
    {
        _hub = hub;
        var listener = new EventBasedNetListener();
        listener.ConnectionRequestEvent += request =>
        {
            // Leave headroom above MaxPlayers so a full server can still answer with a proper "server full" kick.
            if (_net!.ConnectedPeersCount < maxConnections + 4)
                request.AcceptIfKey(ProtocolInfo.ConnectionKey);
            else
                request.Reject();
        };
        listener.PeerConnectedEvent += peer =>
        {
            var connectionId = _hub.RegisterConnection(this);
            _connectionByPeer[peer.Id] = connectionId;
            _peerByConnection[connectionId] = peer;
        };
        listener.PeerDisconnectedEvent += (peer, _) =>
        {
            if (!_connectionByPeer.TryGetValue(peer.Id, out var connectionId))
                return;
            _connectionByPeer.Remove(peer.Id);
            _peerByConnection.Remove(connectionId);
            _hub.ConnectionClosed(connectionId);
        };
        listener.NetworkReceiveEvent += (peer, reader, _, _) =>
        {
            var data = reader.GetRemainingBytes();
            reader.Recycle();
            if (_connectionByPeer.TryGetValue(peer.Id, out var connectionId))
                _hub.Receive(connectionId, data);
        };

        _net = new NetManager(listener)
        {
            AutoRecycle = false,
            UpdateTime = 5,
            DisconnectTimeout = ProtocolInfo.ConnectionTimeoutSeconds * 1000,
        };
    }

    public bool Start(int port) => _net.Start(port);

    public void Poll() => _net.PollEvents();

    public void Send(int connectionId, byte[] data, DeliveryMode mode)
    {
        if (_peerByConnection.TryGetValue(connectionId, out var peer))
            peer.Send(data, LiteNetMapping.ToMethod(mode));
    }

    public void Disconnect(int connectionId, byte[]? farewell = null)
    {
        if (!_peerByConnection.TryGetValue(connectionId, out var peer))
            return;

        // The farewell rides inside LiteNetLib's disconnect packet, so it cannot be lost to the close racing it.
        if (farewell != null)
            peer.Disconnect(farewell);
        else
            peer.Disconnect();
        _peerByConnection.Remove(connectionId);
        _connectionByPeer.Remove(peer.Id);
        _hub.ConnectionClosed(connectionId);
    }

    public void Stop()
    {
        _net.TriggerUpdate();
        _net.Stop();
        foreach (var connectionId in new List<int>(_peerByConnection.Keys))
            _hub.ConnectionClosed(connectionId);
        _peerByConnection.Clear();
        _connectionByPeer.Clear();
    }
}

internal static class LiteNetMapping
{
    public static DeliveryMethod ToMethod(DeliveryMode mode) => mode switch
    {
        DeliveryMode.ReliableOrdered => DeliveryMethod.ReliableOrdered,
        DeliveryMode.UnreliableSequenced => DeliveryMethod.Sequenced,
        _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, null),
    };
}
