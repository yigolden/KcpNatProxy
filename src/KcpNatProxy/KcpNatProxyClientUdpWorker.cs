using System;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace KcpNatProxy
{
    internal sealed class KcpNatProxyClientUdpWorker : KcpNatProxyClientWorker
    {
        private readonly KcpNatProxyClientWorkerDispatcher _dispatcher;
        private readonly int _serviceId;
        private readonly EndPoint _localForwardEndPoint;
        private readonly int _mtu;
        private readonly MemoryPool _memoryPool;
        private readonly ILogger _logger;
        private readonly ConcurrentDictionary<IPEndPoint, KcpNatProxyClientUdpChannelForwarder> _channels;
        private bool _isRunning;
        private CancellationTokenSource? _scanCts;

        public KcpNatProxyClientUdpWorker(KcpNatProxyClientWorkerDispatcher dispatcher, int serviceId, EndPoint localForwardEndPoint, int mtu, MemoryPool memoryPool, ILogger logger)
        {
            _dispatcher = dispatcher;
            _serviceId = serviceId;
            _localForwardEndPoint = localForwardEndPoint;
            _mtu = mtu;
            _memoryPool = memoryPool;
            _logger = logger;
            _channels = new ConcurrentDictionary<IPEndPoint, KcpNatProxyClientUdpChannelForwarder>();
        }

        public override void Start()
        {
            _isRunning = true;
            _scanCts = new CancellationTokenSource();
            _ = Task.Run(() => ScanLoopAscyn(_scanCts));
        }

        public override void Dispose()
        {
            _isRunning = false;
            try
            {
                Interlocked.Exchange(ref _scanCts, null)?.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // Ignore
            }
            while (!_channels.IsEmpty)
            {
                foreach (KeyValuePair<IPEndPoint, KcpNatProxyClientUdpChannelForwarder> item in _channels)
                {
                    if (_channels.TryRemove(item))
                    {
                        item.Value.Dispose();
                    }
                }
            }
        }

        public override int Id => _serviceId;

        public ValueTask SendAsync(Memory<byte> packet, CancellationToken cancellationToken)
            => _dispatcher.SendPacketAsync(packet, cancellationToken);

        public override ValueTask InputPacketAsync(ReadOnlyMemory<byte> packet, CancellationToken cancellationToken)
        {
            ReadOnlySpan<byte> span = packet.Span;
            if (span.Length < 20)
            {
                return default;
            }
            int length = BinaryPrimitives.ReadUInt16LittleEndian(span.Slice(18, 2));
            if (length + 20 > span.Length)
            {
                return default;
            }
            packet = packet.Slice(20, length);
            var ipep = new IPEndPoint(new IPAddress(span.Slice(0, 16)), BinaryPrimitives.ReadUInt16LittleEndian(span.Slice(16, 2)));

            KcpNatProxyClientUdpChannelForwarder? channelForwarder;
            if (_channels.TryGetValue(ipep, out channelForwarder))
            {
                return channelForwarder.SendAsync(packet, cancellationToken);
            }

            var channel = new KcpNatProxyClientUdpChannelForwarder(this, _localForwardEndPoint, ipep, _mtu, _memoryPool, _logger);
            KcpNatProxyClientUdpChannelForwarder addedChannel = _channels.AddOrUpdate(ipep, channel, (_, v) => v);
            if (!_isRunning)
            {
                if (_channels.TryRemove(ipep, out channelForwarder))
                {
                    channelForwarder.Dispose();
                }
                return default;
            }
            if (ReferenceEquals(channel, addedChannel))
            {
                return channel.SendAsync(packet, cancellationToken);
            }
            else
            {
                channel.Dispose();
                return addedChannel.SendAsync(packet, cancellationToken);
            }
        }

        private async Task ScanLoopAscyn(CancellationTokenSource cts)
        {
            CancellationToken cancellationToken = cts.Token;
            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    await Task.Delay(TimeSpan.FromMinutes(1), cancellationToken).ConfigureAwait(false);

                    foreach (KeyValuePair<IPEndPoint, KcpNatProxyClientUdpChannelForwarder> item in _channels)
                    {
                        if (item.Value.IsExpired)
                        {
                            if (_channels.TryRemove(item))
                            {
                                item.Value.Dispose();
                            }
                        }
                    }

                }
            }
            catch (OperationCanceledException)
            {
                // Ignore
            }
            finally
            {
                cts.Dispose();
            }

        }
    }
}
