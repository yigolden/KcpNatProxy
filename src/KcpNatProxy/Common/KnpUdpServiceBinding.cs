using System;
using System.Diagnostics;
using System.Net;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace KcpNatProxy
{
    internal sealed class KnpUdpServiceBinding : IKnpForwardHost, IKnpServiceBinding
    {
        private readonly KnpUdpService _udpService;
        private readonly IKnpConnectionHost _host;
        private readonly KnpRentedInt32 _bindingId;
        private readonly KnpServiceBindingRegistration _registration;

        private int _disposed;

        public string ServiceName => _udpService.ServiceName;
        public int BindingId => _bindingId.Value;
        public int Mss => _host.Mtu - 8;

        public KnpUdpServiceBinding(KnpUdpService udpService, IKnpConnectionHost host, KnpRentedInt32 bindingId)
        {
            _udpService = udpService;
            _host = host;
            _bindingId = bindingId;
            _registration = new KnpServiceBindingRegistration(udpService, this);
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                _registration.Dispose();
                _bindingId.Dispose();
            }
        }

        ValueTask IKnpServiceBinding.InputPacketAsync(ReadOnlyMemory<byte> packet, CancellationToken cancellationToken)
            => default;

        public KnpUdpServiceForwardSession? CreateForwardSession(KnpRentedInt32 forwardId, EndPoint sourceEndPoint, int packetSize)
        {
            if (_disposed != 0)
            {
                forwardId.Dispose();
                return null;
            }
            if (packetSize > Mss)
            {
                forwardId.Dispose();
                return null;
            }

            var forwardSession = new KnpUdpServiceForwardSession(this, _udpService, forwardId, sourceEndPoint);

            Span<byte> buffer = stackalloc byte[8];
            _bindingId.WriteBigEndian(buffer);
            forwardId.Write(buffer.Slice(4));

            if (!_host.TryRegister(MemoryMarshal.Read<long>(buffer), forwardSession))
            {
                forwardSession.Dispose();
                return null;
            }

            forwardSession.Start();
            return forwardSession;
        }

        public ValueTask ForwardBackAsync(KcpBufferList bufferList, CancellationToken cancellationToken)
        {
            if (_disposed != 0)
            {
                return default;
            }

            return _host.SendAsync(bufferList, cancellationToken);
        }
        public void NotifySessionClosed(int forwardId, IKnpForwardSession session)
        {
            Debug.Assert(session.GetType() == typeof(KnpUdpServiceForwardSession));
            var forwardSession = (KnpUdpServiceForwardSession)session;

            Span<byte> buffer = stackalloc byte[8];
            _bindingId.WriteBigEndian(buffer);
            MemoryMarshal.Write(buffer.Slice(4), ref forwardId);

            _host.TryUnregister(MemoryMarshal.Read<long>(buffer), forwardSession);

            _udpService.RemoveEndPoint(forwardSession);
        }

    }
}
