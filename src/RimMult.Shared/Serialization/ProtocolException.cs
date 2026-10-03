using System;

namespace RimMult.Shared.Serialization;

/// <summary>Malformed or unexpected data from the network. The peer that sent it should be dropped.</summary>
public sealed class ProtocolException : Exception
{
    public ProtocolException(string message) : base(message)
    {
    }
}
