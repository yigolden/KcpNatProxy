using System;

namespace KcpNatProxy
{
    internal interface IKnpService : IDisposable
    {
        string ServiceName { get; }
        KnpServiceType ServiceType { get; }
        void Start();
        int WriteParameters(Span<byte> buffer);
        IKnpServiceBinding? CreateBinding(IKnpConnectionHost host, KnpRentedInt32 bindingId, ReadOnlySpan<byte> parameters);
    }
}
