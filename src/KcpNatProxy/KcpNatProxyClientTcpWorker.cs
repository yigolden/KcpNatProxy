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
    internal sealed class KcpNatProxyClientTcpWorker : KcpNatProxyClientWorker
    {
        private readonly KcpNatProxyClientWorkerDispatcher _dispatcher;
        private readonly int _serviceId;
        private readonly EndPoint _localForwardEndPoint;
        private readonly bool _noDelay;
        private readonly int _mtu;
        private readonly MemoryPool _memoryPool;
        private readonly ILogger _logger;
        private readonly ConcurrentDictionary<int, KcpNatProxyClientTcpWorkerTransport> _channels;
        private CancellationTokenSource? _cts;

        public KcpNatProxyClientTcpWorker(KcpNatProxyClientWorkerDispatcher dispatcher, int serviceId, EndPoint localForwardEndPoint,bool noDelay, int mtu, MemoryPool memoryPool, ILogger logger)
        {
            _dispatcher = dispatcher;
            _serviceId = serviceId;
            _localForwardEndPoint = localForwardEndPoint;
            _noDelay = noDelay;
            _mtu = mtu;
            _memoryPool = memoryPool;
            _logger = logger;
            _channels = new ConcurrentDictionary<int, KcpNatProxyClientTcpWorkerTransport>();
        }

        public override void Start()
        {
            _cts = new CancellationTokenSource();
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
        }

        public override int Id => _serviceId;

        public ValueTask SendPacketAsync(Memory<byte> packet, CancellationToken cancellationToken)
        {
            if (packet.Length < 4)
            {
                return default;
            }
            BinaryPrimitives.WriteInt32LittleEndian(packet.Span, _serviceId);
            if (_cts is null)
            {
                return default;
            }
            return _dispatcher.SendPacketAsync(packet, cancellationToken);
        }

        public override ValueTask InputPacketAsync(ReadOnlyMemory<byte> packet, CancellationToken cancellationToken)
        {
            CancellationTokenSource? cts = _cts;
            if (cts is null)
            {
                return default;
            }
            if (!BinaryPrimitives.TryReadInt32LittleEndian(packet.Span, out int channelId))
            {
                return default;
            }

            if (!_channels.TryGetValue(channelId, out KcpNatProxyClientTcpWorkerTransport? channel))
            {
                var transport = new KcpNatProxyClientTcpWorkerTransport(this, _localForwardEndPoint, channelId, _noDelay, _mtu, _memoryPool, _logger);
                channel = _channels.AddOrUpdate(channelId, transport, (_, t) => t);
                if (ReferenceEquals(transport, channel))
                {
                    channel.Start(cts.Token);
                }
            }

            return channel.InputPacketAsync(packet.Slice(4), cancellationToken);
        }

        public void RemoveChannel(int id, KcpNatProxyClientTcpWorkerTransport channel)
        {
            _channels.TryRemove(new KeyValuePair<int, KcpNatProxyClientTcpWorkerTransport>(id, channel));
        }
    }
}
