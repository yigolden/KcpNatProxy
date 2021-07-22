using System;
using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using KcpSharp;

namespace KcpNatProxy
{
    internal sealed class KcpNatProxyServerUdpListener : KcpNatProxyServiceWorker
    {
        private readonly int _id;
        private readonly KcpNatProxyServiceWorkerDispatcher _dispatcher;
        private readonly Socket _socket;
        private readonly int _mtu;
        private readonly MemoryPool _memoryPool;
        private readonly EndPoint _localEndPoint;
        private readonly UdpLivenessTracker _livenessTracker;
        private KcpListenEndPointTrackerHolder _trackHolder;
        private readonly ServiceNameHandle _nameHandle;

        private CancellationTokenSource? _cts;

        public override KcpNatProxyServiceType Type => KcpNatProxyServiceType.Udp;
        public override string Name => _nameHandle.Name;
        public override int Id => _id;

        private KcpNatProxyServerUdpListener(KcpNatProxyServiceWorkerDispatcher dispatcher, KcpListenEndPointTrackerHolder trackHolder, ServiceNameHandle nameHandle, Socket socket, EndPoint localEndPoint, int mtu, MemoryPool memoryPool)
        {
            _id = dispatcher.ServiceIdPool.Rent();
            _dispatcher = dispatcher;
            _trackHolder = trackHolder;
            _nameHandle = nameHandle;
            _socket = socket;
            _mtu = mtu;
            _memoryPool = memoryPool;
            _localEndPoint = localEndPoint;
            _livenessTracker = new UdpLivenessTracker();
        }

        public static KcpNatProxyServerUdpListener? Create(KcpNatProxyServiceWorkerDispatcher dispatcher, ServiceNameHandle nameHandle, IPEndPoint listenEndPoint, int mtu, MemoryPool memoryPool)
        {
            // start tracking
            KcpListenEndPointTrackerHolder trackHolder = dispatcher.EndPointTracker.Track(KcpNatProxyServiceType.Udp, listenEndPoint);
            if (!trackHolder.IsTracking)
            {
                return default;
            }

            // try to start socket
            var socket = new Socket(SocketType.Dgram, ProtocolType.Udp);
            SocketHelper.PatchSocket(socket);
            try
            {
                if (IPAddress.IPv6Any.Equals(listenEndPoint.Address))
                {
                    socket.DualMode = true;
                }
                socket.Bind(listenEndPoint);
            }
            catch (SocketException)
            {
                return default;
            }
            finally
            {
                if (trackHolder.IsTracking)
                {
                    trackHolder.Dispose();
                }
            }

            return new KcpNatProxyServerUdpListener(dispatcher, trackHolder, nameHandle, socket, listenEndPoint, mtu, memoryPool);
        }

        public override void Start()
        {
            if (_cts is not null)
            {
                throw new InvalidOperationException();
            }
            _cts = new CancellationTokenSource();
            _ = Task.Run(() => RunReceiveLoop(_cts));
        }

        private async Task RunReceiveLoop(CancellationTokenSource cts)
        {
            CancellationToken cancellationToken = cts.Token;

            using KcpRentedBuffer memoryHandle = _memoryPool.Rent(new KcpBufferPoolRentOptions(_mtu, true));
            try
            {
                Memory<byte> buffer = memoryHandle.Memory.Slice(0, _mtu);
                BinaryPrimitives.WriteInt32LittleEndian(buffer.Span, _id);
                while (!cancellationToken.IsCancellationRequested)
                {
                    SocketReceiveFromResult result = await _socket.ReceiveFromAsync(buffer.Slice(24, _mtu - 24), SocketFlags.None, _localEndPoint, cancellationToken).ConfigureAwait(false);
                    if (result.RemoteEndPoint is not IPEndPoint ipep)
                    {
                        continue;
                    }
                    IPAddress address = ipep.Address;
                    if (address.AddressFamily == AddressFamily.InterNetwork)
                    {
                        buffer.Span.Slice(4, 16).Clear();
                        buffer.Span.Slice(14, 2).Fill(0xff);
                        address.TryWriteBytes(buffer.Span.Slice(16, 4), out _);
                    }
                    else if (address.AddressFamily == AddressFamily.InterNetworkV6)
                    {
                        address.TryWriteBytes(buffer.Span.Slice(4, 16), out _);
                    }
                    else
                    {
                        continue;
                    }
                    _livenessTracker.Track(address.AddressFamily == AddressFamily.InterNetworkV6 ? ipep : new IPEndPoint(address.MapToIPv6(), ipep.Port));
                    BinaryPrimitives.WriteUInt16LittleEndian(buffer.Span.Slice(20, 2), (ushort)ipep.Port);
                    BinaryPrimitives.WriteUInt16LittleEndian(buffer.Span.Slice(22, 2), (ushort)result.ReceivedBytes);
                    await _dispatcher.SendPacketAsync(buffer.Slice(0, 24 + result.ReceivedBytes), cancellationToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                // Ignore
            }
            catch (ObjectDisposedException)
            {
                // Ignore
            }
            finally
            {
                cts.Dispose();
            }
        }

        public override void Dispose()
        {
            try
            {
                Interlocked.Exchange(ref _cts, null)?.Dispose();
            }
            catch (ObjectDisposedException)
            {
                // Ignore
            }
            if (_trackHolder.IsTracking)
            {
                _socket.Dispose();
                _trackHolder.Dispose();
                _nameHandle.Dispose();
                _livenessTracker.Dispose();
                _trackHolder = default;
                _dispatcher.ServiceIdPool.Return(_id);
            }
        }

        public override async ValueTask InputPacketAsync(ReadOnlyMemory<byte> packet, CancellationToken cancellationToken)
        {
            if (_cts is null)
            {
                return;
            }
            if (!TryParseHeader(packet.Span, out IPEndPoint? ipep, out ushort length))
            {
                return;
            }
            packet = packet.Slice(20);
            if (packet.Length < length)
            {
                return;
            }
            if (!_livenessTracker.Check(ipep))
            {
                return;
            }

            try
            {
                await _socket.SendToAsync(packet.Slice(0, length), SocketFlags.None, ipep, cancellationToken).ConfigureAwait(false);
            }
            catch (ObjectDisposedException)
            {
                // Ignore
            }
        }

        private static bool TryParseHeader(ReadOnlySpan<byte> buffer, [NotNullWhen(true)] out IPEndPoint? ipep, out ushort length)
        {
            if (buffer.Length < 20)
            {
                ipep = null;
                length = default;
                return false;
            }

            var address = new IPAddress(buffer.Slice(0, 16));

            ipep = new IPEndPoint(address, BinaryPrimitives.ReadUInt16LittleEndian(buffer.Slice(16, 2)));
            length = BinaryPrimitives.ReadUInt16LittleEndian(buffer.Slice(18, 2));
            return true;
        }
    }
}
