using System;
using System.Threading;
using System.Threading.Tasks;

namespace KcpNatProxy.Client
{
    internal interface IKnpProvider : IDisposable
    {
        ValueTask InputPacketAsync(ReadOnlyMemory<byte> packet, CancellationToken cancellationToken);
    }
}
