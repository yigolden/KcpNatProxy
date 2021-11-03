using System;
using System.Buffers.Binary;
using System.Net;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace KcpNatProxy.Client
{
    internal sealed class KnpTcpProvider : IKnpProvider, IKnpForwardHost
    {
        private readonly IKnpConnectionHost _host;
        private readonly int _bindingId;
        private readonly EndPoint _forwardEndPoint;
        private readonly KnpTcpKcpParameters _parameters;
        private readonly KnpTcpKcpRemoteParameters _remoteParameters;

        private bool _disposed;

        public int BindingId => _bindingId;
        public int Mss => _host.Mtu - 8; // 8: bindingId + forwardId

        public KnpTcpProvider(IKnpConnectionHost host, int bindingId, EndPoint forwardEndPoint, KnpTcpKcpParameters parameters, KnpTcpKcpRemoteParameters remoteParameters)
        {
            _host = host;
            _bindingId = bindingId;
            _forwardEndPoint = forwardEndPoint;
            _parameters = parameters;
            _remoteParameters = remoteParameters;
        }

        public void Dispose() { _disposed = true; }

        public ValueTask InputPacketAsync(ReadOnlyMemory<byte> packet, CancellationToken cancellationToken)
        {
            if (_disposed || packet.Length <= 8)
            {
                return default;
            }

            if (!KcpPacketHelper.IsFirstPacket(packet.Span.Slice(8)))
            {
                return default;
            }

            int forwardId = MemoryMarshal.Read<int>(packet.Span.Slice(4));

            var forwardSession = new KnpTcpProviderForwardSession(this, _host.BufferPool, forwardId, _forwardEndPoint, _parameters, _remoteParameters, _host.Logger);
            if (!_host.TryRegister(MemoryMarshal.Read<long>(packet.Span.Slice(0, 8)), forwardSession))
            {
                forwardSession.Dispose();
            }

            forwardSession.Start();
            return forwardSession.InputPacketAsync(packet.Slice(8), cancellationToken);
        }

        public ValueTask ForwardBackAsync(KcpBufferList bufferList, CancellationToken cancellationToken)
            => _host.SendAsync(bufferList, cancellationToken);

        public void NotifySessionClosed(int forwardId, IKnpForwardSession session)
        {
            Span<byte> buffer = stackalloc byte[8];
            BinaryPrimitives.WriteInt32BigEndian(buffer, _bindingId);
            MemoryMarshal.Write(buffer.Slice(4), ref forwardId);
            _host.TryUnregister(MemoryMarshal.Read<long>(buffer), session);
        }

    }
}
