using System;
using System.Threading;
using System.Threading.Tasks;

namespace KcpNatProxy
{
    internal interface IKnpServiceBinding : IDisposable
    {
        string ServiceName { get; }
        int BindingId { get; }
        int Mss { get; }
        ValueTask InputPacketAsync(ReadOnlyMemory<byte> packet, CancellationToken cancellationToken);
    }
}
