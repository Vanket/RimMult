using System;
using System.Collections.Generic;
using System.Linq;
using RimMult.Shared.Trade;

namespace RimMult.ClientCore;

public enum TradeState
{
    /// <summary>We invited the partner and wait for them to join.</summary>
    Inviting,

    /// <summary>The partner invited us; we haven't answered yet.</summary>
    Invited,

    /// <summary>Both sides can change and accept offers.</summary>
    Open,

    /// <summary>Both sides committed to the same pair of offers: hand over our side now.</summary>
    Completed,

    Cancelled,
}

/// <summary>
/// One trade negotiation between this player and a partner. Engine-independent: offers are display lines, the game
/// side keeps the actual items and hands them over when <see cref="Completed"/> fires.
/// <para>
/// Safety: every offer carries a version; accepting means "your offer as of version N"; and the trade completes only
/// when both sides have sent a commit naming the same (mine, theirs) pair. A last-second change of either offer
/// invalidates every stale accept and commit, so nobody ends up giving without receiving.
/// </para>
/// </summary>
public sealed class PlayerTrade
{
    private readonly Action<TradeMessage> _send;
    private int _myVersion;
    private int _theirVersion;
    private int _iAccepted = -1;
    private int _theyAccepted = -1;
    private (int Mine, int Theirs)? _myCommit;
    private (int Theirs, int Mine)? _theirCommit;

    public PlayerTrade(int partnerId, string partnerName, TradeState state, Action<TradeMessage> send)
    {
        PartnerId = partnerId;
        PartnerName = partnerName;
        State = state;
        _send = send;
    }

    /// <summary>Something visible changed (offer, acceptance, state).</summary>
    public event Action? Changed;

    /// <summary>Both sides committed: hand over <see cref="MyOffer"/> now. Raised exactly once.</summary>
    public event Action? Completed;

    public int PartnerId { get; }
    public string PartnerName { get; }

    /// <summary>We trade from a caravan standing at the partner's colony (its id in our game), or 0.</summary>
    public int MyCaravanId { get; internal set; }

    /// <summary>The partner trades from a caravan at our colony (its id in their game), or 0.</summary>
    public int PartnerCaravanId { get; internal set; }
    public TradeState State { get; private set; }

    public IReadOnlyList<TradeLine> MyOffer { get; private set; } = Array.Empty<TradeLine>();
    public IReadOnlyList<TradeLine> TheirOffer { get; private set; } = Array.Empty<TradeLine>();

    /// <summary>The partner's showcase: what they could give (count available, value per unit).</summary>
    public IReadOnlyList<TradeLine> TheirCatalog { get; private set; } = Array.Empty<TradeLine>();

    /// <summary>What we asked for from their showcase (key → count).</summary>
    public IReadOnlyDictionary<string, int> MyRequests => _myRequests;

    /// <summary>What the partner asked for from our showcase (key → count).</summary>
    public IReadOnlyDictionary<string, int> TheirRequests { get; private set; } = new Dictionary<string, int>();

    private readonly Dictionary<string, int> _myRequests = new();

    public bool IAccepted => _iAccepted == _theirVersion;
    public bool TheyAccepted => _theyAccepted == _myVersion;

    /// <summary>We committed; our offer can no longer change (the partner may still complete the trade).</summary>
    public bool Locked => _myCommit != null;

    public bool IsActive => State is TradeState.Inviting or TradeState.Invited or TradeState.Open;

    public float MyValue => MyOffer.Sum(l => l.Value);
    public float TheirValue => TheirOffer.Sum(l => l.Value);

    /// <summary>Answer an invitation.</summary>
    public void Join()
    {
        if (State != TradeState.Invited)
            return;
        State = TradeState.Open;
        _send(new TradeMessage { Kind = TradeMessageKind.Join });
        Changed?.Invoke();
    }

    public void SetMyOffer(List<TradeLine> lines)
    {
        if (State != TradeState.Open || Locked)
            return;
        _myVersion++;
        MyOffer = lines;
        _send(new TradeMessage { Kind = TradeMessageKind.Offer, Version = _myVersion, Lines = lines });
        Changed?.Invoke();
    }

    /// <summary>Shows the partner what we could give. Sent again whenever it changes.</summary>
    public void SetCatalog(List<TradeLine> lines)
    {
        if (State != TradeState.Open)
            return;
        _send(new TradeMessage { Kind = TradeMessageKind.Catalog, Lines = lines });
    }

    /// <summary>Asks for <paramref name="count"/> of a line of the partner's showcase (0 takes the wish back).</summary>
    public void Request(string key, int count)
    {
        if (State != TradeState.Open || Locked)
            return;
        var line = TheirCatalog.FirstOrDefault(l => l.Key == key);
        count = Math.Max(0, Math.Min(count, line?.Count ?? 0));
        if (count == 0)
            _myRequests.Remove(key);
        else
            _myRequests[key] = count;
        _send(new TradeMessage
        {
            Kind = TradeMessageKind.Request,
            Lines = _myRequests.Select(p => new TradeLine
            {
                Key = p.Key,
                Label = TheirCatalog.FirstOrDefault(l => l.Key == p.Key)?.Label ?? "",
                Count = p.Value,
            }).ToList(),
        });
        Changed?.Invoke();
    }

    public void SetAccepted(bool accepted)
    {
        if (State != TradeState.Open || Locked)
            return;
        _iAccepted = accepted ? _theirVersion : -1;
        _send(new TradeMessage { Kind = TradeMessageKind.Accept, Version = _iAccepted });
        TryCommit();
        Changed?.Invoke();
    }

    /// <summary>Calls the trade off. Not possible after committing: the partner may already be handing over.</summary>
    public void Cancel()
    {
        if (!IsActive || Locked)
            return;
        State = TradeState.Cancelled;
        _send(new TradeMessage { Kind = TradeMessageKind.Cancel });
        Changed?.Invoke();
    }

    /// <summary>The partner left or disconnected: the trade is off without telling them.</summary>
    public void Abort()
    {
        if (!IsActive)
            return;
        State = TradeState.Cancelled;
        Changed?.Invoke();
    }

    public void Receive(TradeMessage message)
    {
        if (!IsActive)
            return;

        switch (message.Kind)
        {
            case TradeMessageKind.Join when State == TradeState.Inviting:
                State = TradeState.Open;
                break;
            case TradeMessageKind.Cancel:
                State = TradeState.Cancelled;
                break;
            case TradeMessageKind.Offer when State == TradeState.Open:
                // Their offer changed: every accept and commit that referred to the old one is void, on both sides.
                _theirVersion = message.Version;
                TheirOffer = message.Lines;
                _myCommit = null;
                _theirCommit = null;
                break;
            case TradeMessageKind.Accept when State == TradeState.Open:
                _theyAccepted = message.Version;
                TryCommit();
                break;
            case TradeMessageKind.Commit when State == TradeState.Open:
                _theirCommit = (message.Version, message.OtherVersion);
                TryCommit();
                break;
            case TradeMessageKind.Catalog when State == TradeState.Open:
                TheirCatalog = message.Lines;
                // Wishes for lines that are gone (or fewer now) shrink with them.
                foreach (var key in _myRequests.Keys.ToList())
                {
                    var available = message.Lines.FirstOrDefault(l => l.Key == key)?.Count ?? 0;
                    if (available <= 0)
                        _myRequests.Remove(key);
                    else if (_myRequests[key] > available)
                        _myRequests[key] = available;
                }
                break;
            case TradeMessageKind.Request when State == TradeState.Open:
                TheirRequests = message.Lines.Where(l => l.Count > 0).GroupBy(l => l.Key).ToDictionary(g => g.Key, g => g.Sum(l => l.Count));
                break;
            default:
                return;
        }
        Changed?.Invoke();
    }

    private void TryCommit()
    {
        if (State != TradeState.Open)
            return;

        if (_myCommit == null && IAccepted && TheyAccepted)
        {
            _myCommit = (_myVersion, _theirVersion);
            _send(new TradeMessage { Kind = TradeMessageKind.Commit, Version = _myVersion, OtherVersion = _theirVersion });
        }

        if (_myCommit is { } mine && _theirCommit is { } theirs
            && mine.Mine == _myVersion && mine.Theirs == _theirVersion
            && theirs.Theirs == _theirVersion && theirs.Mine == _myVersion)
        {
            State = TradeState.Completed;
            Completed?.Invoke();
        }
    }
}
