using System;
using System.Buffers.Binary;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using KcpSharp;
using Microsoft.Extensions.Logging;

namespace KcpNatProxy
{
    internal sealed class KcpNatProxyServerTcpTransport : IKcpTransport
    {
        private readonly KcpNatProxyServerTcpListener _listener;
        private readonly int _channelId;
        private readonly bool _noDelay;
        private readonly int _mtu;
        private readonly MemoryPool _memoryPool;
        private readonly ILogger _logger;
        private KcpConversation? _conversation;

        public KcpNatProxyServerTcpTransport(KcpNatProxyServerTcpListener listener, int channelId, bool noDelay, int mtu, MemoryPool memoryPool, ILogger logger)
        {
            _listener = listener;
            _channelId = channelId;
            _noDelay = noDelay;
            _mtu = mtu;
            _memoryPool = memoryPool;
            _logger = logger;
        }

        public async Task RunAsync(Socket socket, CancellationToken cancellationToken)
        {
            _conversation = new KcpConversation(this, new KcpConversationOptions { BufferPool = _memoryPool, PreBufferSize = 8, UpdateInterval = 10, FastResend = 2, SendWindow = 1024, SendQueueSize = 2048, ReceiveWindow = 1024, StreamMode = true, NoDelay = _noDelay, Mtu = _mtu });
            try
            {
                // connect phase
                {
                    byte b = 0;
                    using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    cts.CancelAfter(TimeSpan.FromSeconds(20));
                    _conversation.TrySend(MemoryMarshal.CreateSpan(ref b, 1));
                    await _conversation.WaitToReceiveAsync(cts.Token).ConfigureAwait(false);
                    if (!_conversation.TryReceive(MemoryMarshal.CreateSpan(ref b, 1), out _))
                    {
                        return;
                    }
                    if (b != 0)
                    {
                        return;
                    }
                }

                // exchange phase
                {
                    var exchange = new KcpTcpDataExchange(socket, _conversation, _memoryPool, _logger);
                    await exchange.RunAsync(cancellationToken).ConfigureAwait(false);
                }
            }
            finally
            {
                _conversation.Dispose();
                _conversation = null;
            }
        }

        public ValueTask InputPacketAsync(ReadOnlyMemory<byte> packet, CancellationToken cancellationToken)
        {
            KcpConversation? conversation = _conversation;
            if (conversation is not null)
            {
                return conversation.InputPakcetAsync(packet, cancellationToken);
            }
            return default;
        }

        ValueTask IKcpTransport.SendPacketAsync(Memory<byte> packet, CancellationToken cancellationToken)
        {
            if (packet.Length < 8)
            {
                return default;
            }
            BinaryPrimitives.WriteInt32LittleEndian(packet.Span.Slice(4), _channelId);
            return _listener.SendPacketAsync(packet, cancellationToken);
        }
    }
}
