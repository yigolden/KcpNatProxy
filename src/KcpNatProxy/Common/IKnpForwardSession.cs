using System;
using System.Threading;
using System.Threading.Tasks;

namespace KcpNatProxy
{
    internal interface IKnpForwardSession : IDisposable
    {
        bool IsExpired(DateTime utcNow);
        ValueTask InputPacketAsync(ReadOnlyMemory<byte> packet, CancellationToken cancellationToken);
    }
}
