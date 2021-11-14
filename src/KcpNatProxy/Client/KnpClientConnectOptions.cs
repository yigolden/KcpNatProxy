using System;
using System.Diagnostics.CodeAnalysis;
using System.Net;

namespace KcpNatProxy.Client
{
    public class KnpClientConnectOptions
    {
        public string? EndPoint { get; set; }
        public int Mtu { get; set; }

        [MemberNotNullWhen(true, nameof(EndPoint))]
        public bool Validate([NotNullWhen(false)] out string? errorMessage, [NotNullWhen(true)] out EndPoint? endPoint)
        {
            if (string.IsNullOrEmpty(EndPoint))
            {
                errorMessage = "Client connect endpoint is missing.";
                endPoint = null;
                return false;
            }
            if (!EndPointParser.TryParseEndPoint(EndPoint.AsSpan(), out endPoint))
            {
                errorMessage = $"Client connect endpoint is invalid.";
                endPoint = null;
                return false;
            }
            if (Mtu < 512)
            {
                errorMessage = "MTU should be at least 512.";
                endPoint = null;
                return false;
            }
            if (Mtu > 12 * 1024)
            {
                errorMessage = "MTU is too large.";
                endPoint = null;
                return false;
            }

            errorMessage = null;
            return true;
        }
    }
}
