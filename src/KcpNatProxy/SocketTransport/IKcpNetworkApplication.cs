using System;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

namespace KcpNatProxy.SocketTransport
{
    public interface IKcpNetworkApplication
    {
        ValueTask InputPacketAsync(ReadOnlyMemory<byte> packet, EndPoint remoteEndPoint, CancellationToken cancellationToken);
        void SetTransportClosed();
        ValueTask SetTransportClosedAsync();
    }
}
