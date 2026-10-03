namespace RimMult.Shared.Net;

public enum DeliveryMode
{
    /// <summary>Commands, events, anything that must arrive exactly once and in order.</summary>
    ReliableOrdered,

    /// <summary>State that the next packet supersedes (tick grants, pawn positions). Drops and stale packets are discarded.</summary>
    UnreliableSequenced,
}
