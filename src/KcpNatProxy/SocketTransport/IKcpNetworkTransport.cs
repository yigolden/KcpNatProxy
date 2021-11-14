using System;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using KcpSharp;

namespace KcpNatProxy.SocketTransport
{
    public interface IKcpNetworkTransport : IKcpExceptionProducer<IKcpNetworkTransport>, IAsyncDisposable, IDisposable
    {
        KcpSocketNetworkApplicationRegistration Register(EndPoint remoteEndPoint, IKcpNetworkApplication application);
        bool QueuePacket(KcpBufferList packet, EndPoint remoteEndPoint);
        ValueTask QueueAndSendPacketAsync(KcpBufferList packet, EndPoint remoteEndPoint, CancellationToken cancellationToken);
        ValueTask QueueAndSendPacketAsync(ReadOnlyMemory<byte> packet, EndPoint remoteEndPoint, CancellationToken cancellationToken);
    }
}
