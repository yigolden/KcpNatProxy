using System;
using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Text;

namespace KcpNatProxy.Client
{
    public class KnpServiceDescription
    {
        public string? Name { get; set; }
        public string? VirtualBus { get; set; }
        public KnpServiceType ServiceType { get; set; }
        public string? Listen { get; set; }

        // KCP-specific
        public int WindowSize { get; set; }
        public int QueueSize { get; set; }
        public int UpdateInterval { get; set; }
        public bool NoDelay { get; set; } = true;

        [MemberNotNull(nameof(Name), nameof(VirtualBus), nameof(Listen))]
        public void ThrowValidationError()
        {
            if (!Validate(out string? errorMessage, out _))
            {
                throw new InvalidOperationException(errorMessage);
            }
        }

        [MemberNotNullWhen(true, nameof(Name), nameof(VirtualBus), nameof(Listen))]
        public bool Validate([NotNullWhen(false)] out string? errorMessage, [NotNullWhen(true)] out IPEndPoint? ipEndPoint)
        {
            if (string.IsNullOrEmpty(Name))
            {
                errorMessage = "Service name is missing.";
                ipEndPoint = null;
                return false;
            }
            if (Encoding.UTF8.GetByteCount(Name) > 128)
            {
                errorMessage = "Service name is too long.";
                ipEndPoint = null;
                return false;
            }
            if (string.IsNullOrEmpty(VirtualBus))
            {
                errorMessage = "Virtual bus is missing.";
                ipEndPoint = null;
                return false;
            }
            if (Encoding.UTF8.GetByteCount(VirtualBus) > 128)
            {
                errorMessage = "Virtual bus is too long.";
                ipEndPoint = null;
                return false;
            }
            if (ServiceType != KnpServiceType.Tcp && ServiceType != KnpServiceType.Udp)
            {
                errorMessage = $"Service type is invalid for service {Name}.";
                ipEndPoint = null;
                return false;
            }
            if (ServiceType == KnpServiceType.Tcp)
            {
                if (WindowSize < 0 || WindowSize > ushort.MaxValue)
                {
                    errorMessage = $"Window size is invalid for service {Name}.";
                    ipEndPoint = null;
                    return false;
                }
                if (QueueSize < 0 || QueueSize > ushort.MaxValue)
                {
                    errorMessage = $"Queue size is invalid for service {Name}.";
                    ipEndPoint = null;
                    return false;
                }
                if (UpdateInterval < 0 || UpdateInterval > 10 * byte.MaxValue)
                {
                    errorMessage = $"Update interval is invalid for service {Name}.";
                    ipEndPoint = null;
                    return false;
                }
            }
            if (string.IsNullOrEmpty(Listen))
            {
                errorMessage = $"Listen endpoint is missing for service {Name}.";
                ipEndPoint = null;
                return false;
            }
            if (!EndPointParser.TryParseEndPoint(Listen.AsSpan(), out EndPoint? endPoint))
            {
                errorMessage = $"Listen endpoint is invalid for service {Name}.";
                ipEndPoint = null;
                return false;
            }
            if (endPoint is not IPEndPoint ipep)
            {
                errorMessage = $"Listen endpoint is not a valid IP endpoint for service {Name}.";
                ipEndPoint = null;
                return false;
            }

            errorMessage = null;
            ipEndPoint = ipep;
            return true;
        }
    }
}
