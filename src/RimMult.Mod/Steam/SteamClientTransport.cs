using System;
using RimMult.Shared.Net;
using Steamworks;
using UnityEngine;

namespace RimMult.Steam;

/// <summary>Connects to a host over Steam P2P, addressed by the host's SteamID.</summary>
internal sealed class SteamClientTransport : IClientTransport
{
    /// <summary>How long to wait for the host to accept before giving up.</summary>
    private const float ConnectTimeout = 20f;

    private readonly CSteamID _host;
    private Callback<P2PSessionRequest_t>? _sessionRequest;
    private Callback<P2PSessionConnectFail_t>? _connectFail;
    private byte[] _buffer = new byte[4096];
    private bool _connecting;
    private bool _connected;
    private bool _closed;
    private float _startedAt;
    private float _lastHeard;
    private float _lastSent;
    private ushort _outSequence;
    private ushort _inSequence;
    private bool _hasInSequence;
    private string? _pendingFailure;

    public SteamClientTransport(ulong hostSteamId)
    {
        _host = new CSteamID(hostSteamId);
    }

    public event Action? Connected;
    public event Action<byte[]>? Received;
    public event Action<string>? Disconnected;

    public void Start()
    {
        SteamNetworking.AllowP2PPacketRelay(true);
        _sessionRequest = Callback<P2PSessionRequest_t>.Create(request =>
        {
            if (request.m_steamIDRemote == _host)
                SteamNetworking.AcceptP2PSessionWithUser(request.m_steamIDRemote);
        });
        _connectFail = Callback<P2PSessionConnectFail_t>.Create(fail =>
        {
            // Steam callbacks fire outside our Poll; report the failure from the next Poll instead.
            if (fail.m_steamIDRemote == _host)
                _pendingFailure = "Steam could not connect to the host";
        });

        _connecting = true;
        _startedAt = Time.realtimeSinceStartup;
        SendFrame(P2PFrame.Control(P2PFrameKind.Connect), reliable: true);
    }

    public void Send(byte[] data, DeliveryMode mode)
    {
        if (!_connected)
            return;

        if (mode == DeliveryMode.ReliableOrdered)
            SendFrame(P2PFrame.Reliable(data), reliable: true);
        else
            SendFrame(P2PFrame.Sequenced(data, _outSequence++), reliable: false);
    }

    public void Poll()
    {
        if (_closed)
            return;

        var now = Time.realtimeSinceStartup;
        while (!_closed && SteamP2P.TryRead(ref _buffer, out var length, out var remote))
        {
            if (remote == _host)
                Handle(length, now);
        }

        if (_closed)
            return;

        if (_pendingFailure != null)
            Fail(_pendingFailure);
        else if (_connecting && now - _startedAt > ConnectTimeout)
            Fail("The host did not respond");
        else if (_connected && now - _lastHeard > SteamP2P.Timeout)
            Fail("Connection timed out");
        else if (_connected && now - _lastSent > SteamP2P.HeartbeatInterval)
            SendFrame(P2PFrame.Control(P2PFrameKind.Heartbeat), reliable: false);
    }

    public void Close()
    {
        if (_closed)
            return;
        if (_connecting || _connected)
            SendFrame(P2PFrame.Control(P2PFrameKind.Disconnect), reliable: true);

        _closed = true;
        _connecting = false;
        _connected = false;
        _sessionRequest?.Dispose();
        _connectFail?.Dispose();
    }

    private void Handle(int length, float now)
    {
        if (!P2PFrame.TryParse(_buffer, length, out var kind, out var sequence, out var payload))
            return;

        _lastHeard = now;
        switch (kind)
        {
            case P2PFrameKind.Accept when _connecting:
                _connecting = false;
                _connected = true;
                Connected?.Invoke();
                break;
            case P2PFrameKind.Disconnect:
                Fail("The host closed the connection");
                break;
            case P2PFrameKind.Reliable when _connected:
                Received?.Invoke(payload);
                break;
            case P2PFrameKind.Sequenced when _connected:
                if (!_hasInSequence || P2PFrame.IsNewer(sequence, _inSequence))
                {
                    _hasInSequence = true;
                    _inSequence = sequence;
                    Received?.Invoke(payload);
                }
                break;
        }
    }

    private void SendFrame(byte[] frame, bool reliable)
    {
        SteamP2P.Send(_host, frame, reliable);
        _lastSent = Time.realtimeSinceStartup;
    }

    private void Fail(string reason)
    {
        if (_closed)
            return;
        Close();
        Disconnected?.Invoke(reason);
    }
}
