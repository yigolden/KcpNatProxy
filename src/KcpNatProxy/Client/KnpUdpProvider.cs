using System;
using System.Net;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace KcpNatProxy.Client
{
    internal sealed class KnpUdpProvider : IKnpProvider, IKnpForwardHost
    {
        private readonly IKnpConnectionHost _host;
        private readonly int _bindingId;
        private readonly EndPoint _forwardEndPoint;

        private bool _disposed;

        public int BindingId => _bindingId;
        public int Mss => _host.Mtu - 8; // 8: bindingId + forwardId

        public KnpUdpProvider(IKnpConnectionHost host, int bindingId, EndPoint forwardEndPoint)
        {
            _host = host;
            _bindingId = bindingId;
            _forwardEndPoint = forwardEndPoint;
        }


        public void Dispose() { _disposed = true; }

        public ValueTask InputPacketAsync(ReadOnlyMemory<byte> packet, CancellationToken cancellationToken)
        {
            if (_disposed || packet.Length <= 8)
            {
                return default;
            }

            int forwardId = MemoryMarshal.Read<int>(packet.Span.Slice(4));

            var forwardSession = new KnpUdpProviderForwardSession(this, _host.BufferPool, forwardId, _forwardEndPoint, _host.Logger);
            if (!_host.TryRegister(MemoryMarshal.Read<long>(packet.Span.Slice(0, 8)), forwardSession))
            {
                forwardSession.Dispose();
            }

            forwardSession.Start();
            return forwardSession.InputPacketAsync(packet.Slice(8), cancellationToken);
        }

        public ValueTask ForwardBackAsync(KcpBufferList bufferList, CancellationToken cancellationToken)
            => _host.SendAsync(bufferList, cancellationToken);

        public void NotifySessionClosed(int forwardId, IKnpForwardSession session) { }
    }
}
