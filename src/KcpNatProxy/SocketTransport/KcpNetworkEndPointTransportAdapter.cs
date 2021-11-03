using System;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

namespace KcpNatProxy.SocketTransport
{
    public sealed class KcpNetworkEndPointTransportAdapter : IKcpNetworkEndPointTransport
    {
        private readonly IKcpNetworkTransport _transport;
        private readonly EndPoint _remoteEndPoint;
        private KcpExceptionProducerCore<IKcpNetworkEndPointTransport> _exceptionProducer;
        private bool _exceptionHandlerRegistered;

        public KcpNetworkEndPointTransportAdapter(IKcpNetworkTransport transport, EndPoint remoteEndPoint)
        {
            _transport = transport;
            _remoteEndPoint = remoteEndPoint;
            _exceptionProducer = new KcpExceptionProducerCore<IKcpNetworkEndPointTransport>();
        }

        public EndPoint? RemoteEndPoint => _remoteEndPoint;

        public void Dispose() => _transport.Dispose();
        public ValueTask DisposeAsync() => _transport.DisposeAsync();
        public ValueTask QueueAndSendPacketAsync(KcpBufferList packet, CancellationToken cancellationToken) => _transport.QueueAndSendPacketAsync(packet, _remoteEndPoint, cancellationToken);
        public ValueTask QueueAndSendPacketAsync(ReadOnlyMemory<byte> packet, CancellationToken cancellationToken) => _transport.QueueAndSendPacketAsync(packet, _remoteEndPoint, cancellationToken);
        public bool QueuePacket(KcpBufferList packet) => _transport.QueuePacket(packet, _remoteEndPoint);
        public void SetExceptionHandler(Func<Exception, IKcpNetworkEndPointTransport, object?, bool> handler, object? state)
        {
            _exceptionProducer.SetExceptionHandler(handler, state);
            if (!_exceptionHandlerRegistered)
            {
                _exceptionHandlerRegistered = true;
                _transport.SetExceptionHandler((ex, _, state) =>
                {
                    var currentObject = (KcpNetworkEndPointTransportAdapter?)state!;
                    return currentObject._exceptionProducer.RaiseException(currentObject, ex);
                }, this);
            }
        }
    }
}
