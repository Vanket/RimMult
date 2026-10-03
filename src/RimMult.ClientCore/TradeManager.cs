using System;
using System.Linq;
using RimMult.Shared.Packets;
using RimMult.Shared.Serialization;
using RimMult.Shared.Trade;

namespace RimMult.ClientCore;

/// <summary>Player-to-player trades over the relay channel. One trade at a time per player.</summary>
public sealed class TradeManager
{
    private readonly ClientSession _session;

    public TradeManager(ClientSession session)
    {
        _session = session;
        session.RelayReceived += OnRelay;
        session.PlayersChanged += OnPlayersChanged;
    }

    /// <summary>A partner invited us; the UI asks the player (<see cref="PlayerTrade.Join"/> or <see cref="PlayerTrade.Cancel"/>).</summary>
    public event Action<PlayerTrade>? Invited;

    /// <summary>The current (or last) trade; null before the first one.</summary>
    public PlayerTrade? Current { get; private set; }

    public bool Busy => Current is { IsActive: true };

    /// <summary>Starts a trade with <paramref name="partnerId"/>. Fails while another trade is open.</summary>
    public PlayerTrade? Invite(int partnerId)
    {
        if (Busy || partnerId == _session.PlayerId)
            return null;
        var trade = Create(partnerId, TradeState.Inviting);
        Send(partnerId, new TradeMessage { Kind = TradeMessageKind.Invite });
        return trade;
    }

    private PlayerTrade Create(int partnerId, TradeState state)
    {
        var trade = new PlayerTrade(partnerId, _session.NameOf(partnerId), state, message => Send(partnerId, message));
        Current = trade;
        return trade;
    }

    private void Send(int partnerId, TradeMessage message) =>
        _session.SendRelay(partnerId, RelayChannel.Trade, message.Encode());

    private void OnRelay(int senderId, RelayChannel channel, byte[] data)
    {
        if (channel != RelayChannel.Trade)
            return;

        TradeMessage message;
        try
        {
            message = TradeMessage.Decode(data);
        }
        catch (ProtocolException)
        {
            return;
        }

        if (message.Kind == TradeMessageKind.Invite)
        {
            if (Busy)
            {
                // Already trading with someone: turn the newcomer down right away.
                Send(senderId, new TradeMessage { Kind = TradeMessageKind.Cancel });
                return;
            }
            Invited?.Invoke(Create(senderId, TradeState.Invited));
            return;
        }

        if (Current != null && Current.PartnerId == senderId)
            Current.Receive(message);
    }

    private void OnPlayersChanged()
    {
        if (Current is { IsActive: true } trade
            && !_session.Players.Any(p => p.Id == trade.PartnerId && p.InWorld))
        {
            trade.Abort();
        }
    }
}
