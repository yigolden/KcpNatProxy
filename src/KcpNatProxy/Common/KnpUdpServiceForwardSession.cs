using System;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

namespace KcpNatProxy
{
    internal sealed class KnpUdpServiceForwardSession : IKnpForwardSession
    {
        private readonly KnpUdpServiceBinding _host;
        private readonly KnpUdpService _udpService;
        private readonly KnpRentedInt32 _forwardId;
        private readonly EndPoint _sourceEndPoint;
        private long _lastActiveTimeTick;

        private readonly object _stateChangeLock = new();
        private byte[]? _bindingForwardIdBytes;
        private bool _disposed;

        public KnpUdpServiceBinding ServiceBinding => _host;
        public EndPoint EndPoint => _sourceEndPoint;
        public bool IsExpired(long tick) => (long)((ulong)tick - (ulong)Interlocked.Read(ref _lastActiveTimeTick)) > 30 * 1000;

        public KnpUdpServiceForwardSession(KnpUdpServiceBinding host, KnpUdpService udpService, KnpRentedInt32 forwardId, EndPoint sourceEndPoint)
        {
            _host = host;
            _udpService = udpService;
            _forwardId = forwardId;
            _sourceEndPoint = sourceEndPoint;
            _lastActiveTimeTick = Environment.TickCount64;
        }

        public void Start()
        {
            lock (_stateChangeLock)
            {
                if (_disposed)
                {
                    return;
                }

                _bindingForwardIdBytes = new byte[8];
                BinaryPrimitives.WriteInt32BigEndian(_bindingForwardIdBytes, _host.BindingId);
                _forwardId.Write(_bindingForwardIdBytes.AsSpan(4));
            }
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }
            lock (_stateChangeLock)
            {
                if (_disposed)
                {
                    return;
                }
                _disposed = true;

                _bindingForwardIdBytes = null;
            }

            _host.NotifySessionClosed(_forwardId.Value, this);
            _forwardId.Dispose();
        }

        public ValueTask InputPacketAsync(ReadOnlyMemory<byte> packet, CancellationToken cancellationToken)
        {
            Interlocked.Exchange(ref _lastActiveTimeTick, Environment.TickCount64);
            return _udpService.SendPakcetAsync(packet, _sourceEndPoint, cancellationToken);
        }

        public ValueTask ForwardAsync(Memory<byte> packet, CancellationToken cancellationToken)
        {
            Interlocked.Exchange(ref _lastActiveTimeTick, Environment.TickCount64);
            if (packet.Length > _host.Mss)
            {
                return default;
            }

            Debug.Assert(packet.Length > 8);
            byte[]? bindingForwardIdBytes = Volatile.Read(ref _bindingForwardIdBytes);
            if (bindingForwardIdBytes is null)
            {
                return default;
            }
            bindingForwardIdBytes.CopyTo(packet);

            using var bufferList = KcpRentedBufferList.Allocate(packet);
            return _host.ForwardBackAsync(bufferList, cancellationToken);
        }

    }
}
