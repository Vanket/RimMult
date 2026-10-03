namespace RimMult.Shared.Net;

public static class ServerAddress
{
    /// <summary>"host", "host:port", "[ipv6]:port". The port defaults to <see cref="ProtocolInfo.DefaultPort"/>.</summary>
    public static bool TryParse(string address, out string host, out int port)
    {
        address = address.Trim();
        host = address;
        port = ProtocolInfo.DefaultPort;
        if (address.Length == 0)
            return false;

        string? portText = null;
        if (address.StartsWith("["))
        {
            var end = address.IndexOf(']');
            if (end < 0)
                return false;
            host = address.Substring(1, end - 1);
            if (end + 1 < address.Length)
            {
                if (address[end + 1] != ':')
                    return false;
                portText = address.Substring(end + 2);
            }
        }
        else if (address.IndexOf(':') is var colon and >= 0 && colon == address.LastIndexOf(':'))
        {
            host = address.Substring(0, colon);
            portText = address.Substring(colon + 1);
        }

        if (portText != null && (!int.TryParse(portText, out port) || port is < 1 or > 65535))
            return false;
        return host.Length > 0;
    }
}
