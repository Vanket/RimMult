using LiteNetLib;
using RimMult.ServerCore;
using RimMult.Shared;

namespace RimMult.Server;

/// <summary>UDP transport for the dedicated server. All callbacks fire inside <see cref="Poll"/>, on the caller's thread.</summary>
public sealed class LiteNetServerTransport : IServerTransport
{
    private readonly NetManager _net;
    private readonly Dictionary<int, NetPeer> _peers = new();
    private GameServer? _server;

    public LiteNetServerTransport(int maxConnections)
    {
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
            _peers[peer.Id] = peer;
            _server?.OnConnected(peer.Id);
        };
        listener.PeerDisconnectedEvent += (peer, _) =>
        {
            _peers.Remove(peer.Id);
            _server?.OnDisconnected(peer.Id);
        };
        listener.NetworkReceiveEvent += (peer, reader, _, _) =>
        {
            var data = reader.GetRemainingBytes();
            reader.Recycle();
            _server?.OnData(peer.Id, data, 0, data.Length);
        };

        _net = new NetManager(listener)
        {
            AutoRecycle = false,
            UpdateTime = 5,
            DisconnectTimeout = 15_000,
        };
    }

    public void Attach(GameServer server) => _server = server;

    public bool Start(int port) => _net.Start(port);

    public void Poll() => _net.PollEvents();

    public void Stop() => _net.Stop();

    public void Send(int connectionId, byte[] data, DeliveryMode mode)
    {
        if (!_peers.TryGetValue(connectionId, out var peer))
            return;

        peer.Send(data, mode switch
        {
            DeliveryMode.ReliableOrdered => DeliveryMethod.ReliableOrdered,
            DeliveryMode.UnreliableSequenced => DeliveryMethod.Sequenced,
            _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, null),
        });
    }

    public void Disconnect(int connectionId)
    {
        // Flush first so a queued Kick packet goes out before the disconnect.
        if (_peers.TryGetValue(connectionId, out var peer))
        {
            _net.TriggerUpdate();
            peer.Disconnect();
        }
    }
}
