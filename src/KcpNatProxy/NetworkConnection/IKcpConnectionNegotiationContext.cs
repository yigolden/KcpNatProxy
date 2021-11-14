using System;

namespace KcpNatProxy.NetworkConnection
{
    public interface IKcpConnectionNegotiationContext
    {
        int? NegotiatedMtu { get; }
        void PutNegotiationData(ReadOnlySpan<byte> data);
        KcpConnectionNegotiationResult MoveNext(Span<byte> data);
    }
}
