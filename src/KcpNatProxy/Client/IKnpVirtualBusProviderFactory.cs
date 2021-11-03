using System;

namespace KcpNatProxy.Client
{
    internal interface IKnpVirtualBusProviderFactory
    {
        string Name { get; }
        KnpServiceType ServiceType { get; }
        int SerializeParameters(Span<byte> buffer);
        IKnpProvider CreateProvider(IKnpConnectionHost host, int bindingId, ReadOnlySpan<byte> remoteParameters);
    }
}
