using System;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using KcpSharp;

namespace KcpNatProxy.SocketTransport
{
    public interface IKcpNetworkEndPointTransport : IKcpExceptionProducer<IKcpNetworkEndPointTransport>, IAsyncDisposable, IDisposable
    {
        EndPoint? RemoteEndPoint { get; }
        bool QueuePacket(KcpBufferList packet);
        ValueTask QueueAndSendPacketAsync(KcpBufferList packet, CancellationToken cancellationToken);
        ValueTask QueueAndSendPacketAsync(ReadOnlyMemory<byte> packet, CancellationToken cancellationToken);
    }
}
