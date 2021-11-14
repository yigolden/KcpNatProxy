using System;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Net;

namespace KcpNatProxy
{
    internal static class EndPointParser
    {
        internal static bool TryParseEndPoint(ReadOnlySpan<char> expression, [NotNullWhen(true)] out EndPoint? endPoint)
        {
            if (expression.Length < 3)
            {
                endPoint = null;
                return false;
            }
            int pos = expression.LastIndexOf(':');
            if (pos <= 0)
            {
                endPoint = null;
                return false;
            }
            ReadOnlySpan<char> portPart = expression.Slice(pos + 1);
            if (portPart.IsEmpty)
            {
                endPoint = null;
                return false;
            }
            if (!ushort.TryParse(portPart, NumberStyles.None, CultureInfo.InvariantCulture, out ushort port))
            {
                endPoint = null;
                return false;
            }
            ReadOnlySpan<char> domainPart = expression.Slice(0, pos);
            if (domainPart.IsEmpty)
            {
                endPoint = null;
                return false;
            }
            if (IPAddress.TryParse(domainPart, out IPAddress? address))
            {
                endPoint = new IPEndPoint(address, port);
            }
            else
            {
                endPoint = new DnsEndPoint(domainPart.ToString(), port);
            }
            return true;
        }
    }
}
