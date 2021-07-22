using System;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace KcpNatProxy
{
    internal sealed class KcpNatProxyServerTcpListener : KcpNatProxyServiceWorker
    {
        private readonly int _id;
        private readonly KcpNatProxyServiceWorkerDispatcher _dispatcher;
        private KcpListenEndPointTrackerHolder _trackHolder;
        private readonly Socket _socket;
        private readonly bool _noDelay;
        private readonly int _mtu;
        private readonly MemoryPool _memoryPool;
        private readonly ILogger _logger;
        private readonly ServiceNameHandle _nameHandle;

        private readonly Int32IdPool _channelIdPool;
        private readonly ConcurrentDictionary<int, KcpNatProxyServerTcpTransport> _channels;
        private CancellationTokenSource? _cts;

        public override KcpNatProxyServiceType Type => KcpNatProxyServiceType.Tcp;
        public override string Name => _nameHandle.Name;
        public override int Id => _id;

        private KcpNatProxyServerTcpListener(KcpNatProxyServiceWorkerDispatcher dispatcher, KcpListenEndPointTrackerHolder trackHolder, ServiceNameHandle nameHandle, Socket socket, bool noDelay, int mtu, MemoryPool memoryPool, ILogger logger)
        {
            _id = dispatcher.ServiceIdPool.Rent();
            _dispatcher = dispatcher;
            _trackHolder = trackHolder;
            _nameHandle = nameHandle;
            _socket = socket;
            _noDelay = noDelay;
            _mtu = mtu;
            _memoryPool = memoryPool;
            _logger = logger;
            _channelIdPool = new Int32IdPool();
            _channels = new ConcurrentDictionary<int, KcpNatProxyServerTcpTransport>();
        }

        public static KcpNatProxyServerTcpListener? Create(KcpNatProxyServiceWorkerDispatcher dispatcher, ServiceNameHandle nameHandle, IPEndPoint listenEndPoint, bool noDelay, int mtu, MemoryPool memoryPool, ILogger logger)
        {
            // start tracking
            KcpListenEndPointTrackerHolder trackHolder = dispatcher.EndPointTracker.Track(KcpNatProxyServiceType.Tcp, listenEndPoint);
            if (!trackHolder.IsTracking)
            {
                return default;
            }

            // try to start socket
            var socket = new Socket(SocketType.Stream, ProtocolType.Tcp);
            try
            {
                if (IPAddress.IPv6Any.Equals(listenEndPoint.Address))
                {
                    socket.DualMode = true;
                }
                socket.Bind(listenEndPoint);
                socket.Listen();
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

            return new KcpNatProxyServerTcpListener(dispatcher, trackHolder, nameHandle, socket, noDelay, mtu, memoryPool, logger);
        }

        public override void Start()
        {
            if (_cts is not null)
            {
                throw new InvalidOperationException();
            }
            _cts = new CancellationTokenSource();
            _ = Task.Run(() => RunAcceptLoop(_cts));

        }

        private async Task RunAcceptLoop(CancellationTokenSource cts)
        {
            CancellationToken cancellationToken = cts.Token;
            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    Socket socket = await _socket.AcceptAsync().ConfigureAwait(false);
                    socket.NoDelay = _noDelay;
                    _ = ProcessSocketAsync(socket, cancellationToken);
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

        private async Task ProcessSocketAsync(Socket socket, CancellationToken cancellationToken)
        {
            int channelId = _channelIdPool.Rent();
            KcpNatProxyServerTcpTransport? transport = null;
            try
            {
                transport = new KcpNatProxyServerTcpTransport(this, channelId, _noDelay, _mtu, _memoryPool, _logger);
                if (!_channels.TryAdd(channelId, transport))
                {
                    return;
                }

                await transport.RunAsync(socket, cancellationToken).ConfigureAwait(false);
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
                if (transport is not null)
                {
                    _channels.TryRemove(new KeyValuePair<int, KcpNatProxyServerTcpTransport>(channelId, transport));
                }
                socket.Dispose();

                if (!cancellationToken.IsCancellationRequested)
                {
                    await Task.Delay(TimeSpan.FromSeconds(10), cancellationToken).ConfigureAwait(false);
                    _channelIdPool.Return(channelId);
                }
            }
        }

        public override void Dispose()
        {
            try
            {
                Interlocked.Exchange(ref _cts, null)?.Cancel();
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
                _trackHolder = default;
                _dispatcher.ServiceIdPool.Return(_id);
            }
        }

        public ValueTask SendPacketAsync(Memory<byte> packet, CancellationToken cancellationToken)
        {
            if (packet.Length < 4)
            {
                return default;
            }
            BinaryPrimitives.WriteInt32LittleEndian(packet.Span, _id);
            if (_cts is null)
            {
                return default;
            }
            return _dispatcher.SendPacketAsync(packet, cancellationToken);
        }

        public override ValueTask InputPacketAsync(ReadOnlyMemory<byte> packet, CancellationToken cancellationToken)
        {
            if (_cts is null)
            {
                return default;
            }
            if (!BinaryPrimitives.TryReadInt32LittleEndian(packet.Span, out int channelId))
            {
                return default;
            }

            if (!_channels.TryGetValue(channelId, out KcpNatProxyServerTcpTransport? channel))
            {
                return default;
            }

            return channel.InputPacketAsync(packet.Slice(4), cancellationToken);
        }

    }
}
