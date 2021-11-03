using System;
using System.Threading;
using System.Threading.Tasks;

namespace KcpNatProxy.NetworkConnection
{
    public interface IKcpNetworkConnectionCallback
    {
        ValueTask PacketReceivedAsync(ReadOnlyMemory<byte> packet, CancellationToken cancellationToken);
        void NotifyStateChanged(IKcpNetworkConnection connection);
    }
}
