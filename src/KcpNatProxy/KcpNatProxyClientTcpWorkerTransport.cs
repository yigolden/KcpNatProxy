using System;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using KcpSharp;
using Microsoft.Extensions.Logging;

namespace KcpNatProxy
{
    internal sealed class KcpNatProxyClientTcpWorkerTransport : IKcpTransport
    {
        private readonly KcpNatProxyClientTcpWorker _worker;
        private readonly EndPoint _forwardEndPoint;
        private readonly int _channelId;
        private readonly bool _noDelay;
        private readonly int _mtu;
        private readonly MemoryPool _memoryPool;
        private readonly ILogger _logger;
        private KcpConversation? _conversation;

        public KcpNatProxyClientTcpWorkerTransport(KcpNatProxyClientTcpWorker worker, EndPoint forwardEndPoint, int channelId, bool noDelay, int mtu, MemoryPool memoryPool, ILogger logger)
        {
            _worker = worker;
            _forwardEndPoint = forwardEndPoint;
            _channelId = channelId;
            _noDelay = noDelay;
            _mtu = mtu;
            _memoryPool = memoryPool;
            _logger = logger;
        }

        public void Start(CancellationToken cancellationToken)
        {
            _conversation = new KcpConversation(this, new KcpConversationOptions { BufferPool = _memoryPool, PreBufferSize = 8, UpdateInterval = 10, FastResend = 2, SendWindow = 1024, SendQueueSize = 2048, ReceiveWindow = 1024, StreamMode = true, NoDelay = _noDelay, Mtu = _mtu });
            _ = Task.Run(() => RunAsync(cancellationToken), CancellationToken.None);
        }

        private async Task RunAsync(CancellationToken cancellationToken)
        {
            Debug.Assert(_conversation is not null);
            Socket? socket = null;
            try
            {
                // connect to remote host
                {
                    Unsafe.SkipInit(out byte b);
                    using var timeoutToken = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    timeoutToken.CancelAfter(TimeSpan.FromSeconds(15));
                    KcpConversationReceiveResult result = await _conversation.WaitToReceiveAsync(timeoutToken.Token).ConfigureAwait(false);
                    if (result.TransportClosed)
                    {
                        return;
                    }
                    if (!_conversation.TryReceive(MemoryMarshal.CreateSpan(ref b, 1), out result) || b != 0)
                    {
                        return;
                    }
                    socket = new Socket(SocketType.Stream, ProtocolType.Tcp);
                    socket.NoDelay = true;
                    try
                    {
                        await socket.ConnectAsync(_forwardEndPoint, timeoutToken.Token).ConfigureAwait(false);
                        b = 0;
                        _conversation.TrySend(MemoryMarshal.CreateSpan(ref b, 1));
                    }
                    catch (Exception ex)
                    {
                        if (!cancellationToken.IsCancellationRequested)
                        {
                            if (timeoutToken.IsCancellationRequested)
                            {
                                Log.LogClientTcpConnectTimeout(_logger);
                            }
                            else
                            {
                                Log.LogClientTcpConnectFailed(_logger, ex);
                            }
                        }
                        using var replyTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                        replyTimeout.CancelAfter(TimeSpan.FromSeconds(10));
                        b = 1;
                        _conversation.TrySend(MemoryMarshal.CreateSpan(ref b, 1));
                        await _conversation.FlushAsync(replyTimeout.Token).ConfigureAwait(false);
                        return;
                    }
                }

                // connection is established
                {
                    var exchange = new KcpTcpDataExchange(socket, _conversation, _memoryPool, _logger);
                    await exchange.RunAsync(cancellationToken).ConfigureAwait(false);
                }
            }
            finally
            {
                socket?.Dispose();
                _conversation.Dispose();
                _conversation = null;

                _worker.RemoveChannel(_channelId, this);
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
            return _worker.SendPacketAsync(packet, cancellationToken);
        }
    }
}
