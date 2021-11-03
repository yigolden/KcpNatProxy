using System;
using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Text;

namespace KcpNatProxy.Client
{
    public class KnpProviderDescription
    {
        public string? Name { get; set; }
        public string? VirtualBus { get; set; }
        public KnpServiceType ServiceType { get; set; }
        public string? Forward { get; set; }

        // KCP-specific
        public int WindowSize { get; set; }
        public int QueueSize { get; set; }
        public int UpdateInterval { get; set; }
        public bool NoDelay { get; set; } = true;

        [MemberNotNull(nameof(Name), nameof(Forward))]
        public void ThrowValidationError()
        {
            if (!Validate(out string? errorMessage, out _))
            {
                throw new InvalidOperationException(errorMessage);
            }
        }

        [MemberNotNullWhen(true, nameof(Name), nameof(Forward))]
        public bool Validate([NotNullWhen(false)] out string? errorMessage, [NotNullWhen(true)] out EndPoint? endPoint)
        {
            if (string.IsNullOrEmpty(Name))
            {
                errorMessage = "Service name is missing.";
                endPoint = null;
                return false;
            }
            if (Encoding.UTF8.GetByteCount(Name) > 128)
            {
                errorMessage = "Service name is too long.";
                endPoint = null;
                return false;
            }
            if (ServiceType != KnpServiceType.Tcp && ServiceType != KnpServiceType.Udp)
            {
                errorMessage = $"Service type is invalid for service {Name}.";
                endPoint = null;
                return false;
            }
            if (ServiceType == KnpServiceType.Tcp)
            {
                if (WindowSize < 0 || WindowSize > ushort.MaxValue)
                {
                    errorMessage = $"Window size is invalid for service {Name}.";
                    endPoint = null;
                    return false;
                }
                if (QueueSize < 0 || QueueSize > ushort.MaxValue)
                {
                    errorMessage = $"Queue size is invalid for service {Name}.";
                    endPoint = null;
                    return false;
                }
                if (UpdateInterval < 0 || UpdateInterval > 10 * byte.MaxValue)
                {
                    errorMessage = $"Update interval is invalid for service {Name}.";
                    endPoint = null;
                    return false;
                }
            }
            if (string.IsNullOrEmpty(Forward))
            {
                errorMessage = $"Forward endpoint is missing for service {Name}.";
                endPoint = null;
                return false;
            }
            if (!EndPointParser.TryParseEndPoint(Forward.AsSpan(), out endPoint))
            {
                errorMessage = $"Forward endpoint is invalid for service {Name}.";
                endPoint = null;
                return false;
            }

            errorMessage = null;
            return true;
        }
    }
}
