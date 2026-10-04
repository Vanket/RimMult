using System.Collections.Generic;
using System.Linq;
using RimMult.ServerCore;
using RimMult.Shared.Net;
using Steamworks;
using UnityEngine;

namespace RimMult.Steam;

/// <summary>Accepts players over Steam P2P. Connection handling follows <see cref="P2PFrame"/>.</summary>
internal sealed class SteamServerEndpoint : IServerEndpoint
{
    private readonly TransportHub _hub;
    private readonly Dictionary<ulong, Peer> _bySteamId = new();
    private readonly Dictionary<int, Peer> _byConnection = new();
    private Callback<P2PSessionRequest_t>? _sessionRequest;
    private Callback<P2PSessionConnectFail_t>? _connectFail;
    private byte[] _buffer = new byte[4096];

    public SteamServerEndpoint(TransportHub hub)
    {
        _hub = hub;
        SteamNetworking.AllowP2PPacketRelay(true);
        // Anyone who knows our SteamID may knock; the handshake (password, mods, versions) decides who stays.
        _sessionRequest = Callback<P2PSessionRequest_t>.Create(request =>
            SteamNetworking.AcceptP2PSessionWithUser(request.m_steamIDRemote));
        _connectFail = Callback<P2PSessionConnectFail_t>.Create(fail =>
        {
            if (_bySteamId.TryGetValue(fail.m_steamIDRemote.m_SteamID, out var peer))
                Drop(peer);
        });
    }

    public void Poll()
    {
        var now = Time.realtimeSinceStartup;
        while (SteamP2P.TryRead(ref _buffer, out var length, out var remote))
            Handle(remote, length, now);

        foreach (var peer in _byConnection.Values.ToList())
        {
            if (now - peer.LastHeard > SteamP2P.Timeout)
                Drop(peer);
            else if (now - peer.LastSent > SteamP2P.HeartbeatInterval)
                SendFrame(peer, P2PFrame.Control(P2PFrameKind.Heartbeat), reliable: false);
        }
    }

    public void Send(int connectionId, byte[] data, DeliveryMode mode)
    {
        if (!_byConnection.TryGetValue(connectionId, out var peer))
            return;

        if (mode == DeliveryMode.ReliableOrdered)
        {
            foreach (var frame in P2PFrame.ReliableFrames(data))
                SendFrame(peer, frame, reliable: true);
        }
        else
            SendFrame(peer, P2PFrame.Sequenced(data, peer.OutSequence++), reliable: false);
    }

    public void Disconnect(int connectionId, byte[]? farewell = null)
    {
        if (!_byConnection.TryGetValue(connectionId, out var peer))
            return;

        // Reliable frames arrive in order, so the farewell is processed before the disconnect.
        // The Steam session itself is left to expire: closing it now could drop both frames still in flight.
        if (farewell != null)
        {
            foreach (var frame in P2PFrame.ReliableFrames(farewell))
                SendFrame(peer, frame, reliable: true);
        }
        SendFrame(peer, P2PFrame.Control(P2PFrameKind.Disconnect), reliable: true);
        Drop(peer);
    }

    public void Stop()
    {
        foreach (var peer in _byConnection.Values.ToList())
        {
            SendFrame(peer, P2PFrame.Control(P2PFrameKind.Disconnect), reliable: true);
            Drop(peer);
        }

        _sessionRequest?.Dispose();
        _connectFail?.Dispose();
        _sessionRequest = null;
        _connectFail = null;
    }

    private void Handle(CSteamID remote, int length, float now)
    {
        if (!P2PFrame.TryParse(_buffer, length, out var kind, out var sequence, out var payload))
            return;

        _bySteamId.TryGetValue(remote.m_SteamID, out var peer);

        // A Connect with a new nonce from a known SteamID is a fresh start (their game restarted or they pressed
        // "try again"): the old connection is dead even if it hasn't timed out yet.
        if (peer != null && kind == P2PFrameKind.Connect && P2PFrame.ConnectNonce(payload) != peer.Nonce)
        {
            Drop(peer);
            peer = null;
        }

        if (peer == null)
        {
            if (kind != P2PFrameKind.Connect)
                return;

            peer = new Peer(remote) { LastHeard = now, Nonce = P2PFrame.ConnectNonce(payload) };
            _bySteamId[remote.m_SteamID] = peer;
            SteamKeepAlive.Add(remote);
            peer.ConnectionId = _hub.RegisterConnection(this);
            _byConnection[peer.ConnectionId] = peer;
        }

        peer.LastHeard = now;
        switch (kind)
        {
            case P2PFrameKind.Connect:
                // Also answers duplicates, in case our first Accept was lost.
                SendFrame(peer, P2PFrame.Control(P2PFrameKind.Accept), reliable: true);
                break;
            case P2PFrameKind.Disconnect:
                Drop(peer);
                break;
            case P2PFrameKind.Reliable:
                _hub.Receive(peer.ConnectionId, payload);
                break;
            case P2PFrameKind.ReliablePart:
                byte[]? message;
                try
                {
                    message = peer.Parts.Add(payload, last: sequence == 1);
                }
                catch (Shared.Serialization.ProtocolException)
                {
                    Drop(peer);
                    return;
                }
                if (message != null)
                    _hub.Receive(peer.ConnectionId, message);
                break;
            case P2PFrameKind.Sequenced:
                if (!peer.HasInSequence || P2PFrame.IsNewer(sequence, peer.InSequence))
                {
                    peer.HasInSequence = true;
                    peer.InSequence = sequence;
                    _hub.Receive(peer.ConnectionId, payload);
                }
                break;
        }
    }

    private void SendFrame(Peer peer, byte[] frame, bool reliable)
    {
        SteamP2P.Send(peer.SteamId, frame, reliable);
        peer.LastSent = Time.realtimeSinceStartup;
    }

    private void Drop(Peer peer)
    {
        if (_bySteamId.Remove(peer.SteamId.m_SteamID))
            SteamKeepAlive.Remove(peer.SteamId);
        if (_byConnection.Remove(peer.ConnectionId))
            _hub.ConnectionClosed(peer.ConnectionId);
    }

    private sealed class Peer
    {
        public Peer(CSteamID steamId)
        {
            SteamId = steamId;
        }

        public CSteamID SteamId { get; }
        public int ConnectionId { get; set; }
        public float LastHeard { get; set; }
        public float LastSent { get; set; }
        public uint Nonce { get; set; }
        public P2PReassembler Parts { get; } = new();
        public ushort OutSequence { get; set; }
        public ushort InSequence { get; set; }
        public bool HasInSequence { get; set; }
    }
}
