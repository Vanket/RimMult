using System;
using LiteNetLib;
using RimMult.Shared;
using RimMult.Shared.Net;

namespace RimMult.Net.LiteNet;

/// <summary>Connects to a server by address and UDP port (dedicated servers, hosts with a forwarded port).</summary>
public sealed class LiteNetClientTransport : IClientTransport
{
    private readonly string _host;
    private readonly int _port;
    private readonly NetManager _net;
    private NetPeer? _server;
    private bool _closed;

    public LiteNetClientTransport(string host, int port)
    {
        _host = host;
        _port = port;

        var listener = new EventBasedNetListener();
        listener.PeerConnectedEvent += peer =>
        {
            _server = peer;
            Connected?.Invoke();
        };
        listener.PeerDisconnectedEvent += (_, info) =>
        {
            // A kick arrives as data attached to the disconnect packet.
            if (info.AdditionalData != null && info.AdditionalData.AvailableBytes > 0 && !_closed)
                Received?.Invoke(info.AdditionalData.GetRemainingBytes());
            Fail(DescribeDisconnect(info));
        };
        listener.NetworkReceiveEvent += (_, reader, _, _) =>
        {
            var data = reader.GetRemainingBytes();
            reader.Recycle();
            Received?.Invoke(data);
        };

        _net = new NetManager(listener)
        {
            AutoRecycle = false,
            UpdateTime = 5,
            DisconnectTimeout = 15_000,
        };
    }

    public event Action? Connected;
    public event Action<byte[]>? Received;
    public event Action<string>? Disconnected;

    public void Start()
    {
        if (!_net.Start())
        {
            Fail("Could not open a UDP socket");
            return;
        }

        try
        {
            // Resolves host names synchronously; fine for a one-off connect click.
            _net.Connect(_host, _port, ProtocolInfo.ConnectionKey);
        }
        catch (Exception e)
        {
            Fail($"Could not resolve {_host}: {e.Message}");
        }
    }

    public void Send(byte[] data, DeliveryMode mode) => _server?.Send(data, LiteNetMapping.ToMethod(mode));

    public void Poll()
    {
        if (!_closed)
            _net.PollEvents();
    }

    public void Close()
    {
        if (_closed)
            return;
        _closed = true;
        _net.TriggerUpdate();
        _net.Stop();
    }

    private void Fail(string reason)
    {
        if (_closed)
            return;
        Close();
        Disconnected?.Invoke(reason);
    }

    private static string DescribeDisconnect(DisconnectInfo info) => info.Reason switch
    {
        DisconnectReason.ConnectionFailed => "Could not reach the server",
        DisconnectReason.Timeout => "Connection timed out",
        DisconnectReason.ConnectionRejected => "The server rejected the connection",
        DisconnectReason.RemoteConnectionClose => "The server closed the connection",
        DisconnectReason.HostUnreachable or DisconnectReason.NetworkUnreachable => "The server is unreachable",
        _ => $"Disconnected ({info.Reason})",
    };
}
