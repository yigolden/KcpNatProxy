using System;
using System.Diagnostics.CodeAnalysis;
using System.Net;

namespace KcpNatProxy.Server
{
    public class KnpServerListenOptions
    {
        public string? EndPoint { get; set; }
        public int Mtu { get; set; }

        [MemberNotNullWhen(true, nameof(EndPoint))]
        public bool Validate([NotNullWhen(false)] out string? errorMessage, [NotNullWhen(true)] out IPEndPoint? ipEndPoint)
        {
            if (string.IsNullOrEmpty(EndPoint))
            {
                errorMessage = "Server listen endpoint is missing.";
                ipEndPoint = null;
                return false;
            }
            if (!EndPointParser.TryParseEndPoint(EndPoint.AsSpan(), out EndPoint? endPoint))
            {
                errorMessage = $"Server listen endpoint is invalid.";
                ipEndPoint = null;
                return false;
            }
            if (endPoint is not IPEndPoint ipep)
            {
                errorMessage = $"Server listen endpoint is not a valid IP endpoint.";
                ipEndPoint = null;
                return false;
            }
            if (Mtu < 512)
            {
                errorMessage = "MTU should be at least 512.";
                ipEndPoint = null;
                return false;
            }
            if (Mtu > 12 * 1024)
            {
                errorMessage = "MTU is too large.";
                ipEndPoint = null;
                return false;
            }

            errorMessage = null;
            ipEndPoint = ipep;
            return true;
        }
    }
}
