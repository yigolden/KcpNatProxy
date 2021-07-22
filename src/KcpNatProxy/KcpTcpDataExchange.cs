using System;
using System.Buffers.Binary;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using KcpSharp;
using Microsoft.Extensions.Logging;

namespace KcpNatProxy
{
    internal sealed class KcpTcpDataExchange
    {
        private readonly Socket _socket;
        private readonly KcpConversation _conversation;
        private readonly MemoryPool _memoryPool;
        private readonly ILogger _logger;

        public KcpTcpDataExchange(Socket socket, KcpConversation conversation, MemoryPool memoryPool, ILogger logger)
        {
            _socket = socket;
            _conversation = conversation;
            _memoryPool = memoryPool;
            _logger = logger;

            conversation.SetExceptionHandler((ex, _, state) => Log.LogServerTcpTransportUnhandledException(((ILogger?)state)!, ex), logger);
        }

        public async Task RunAsync(CancellationToken cancellationToken)
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            Task kcpToTcp = Task.Run(() => PumpFromKcpToTcp(cts.Token), cts.Token);
            Task tcpToKcp = Task.Run(() => PumpFromTcpToKcp(cts.Token), cts.Token);
            Task finishedTask = await Task.WhenAny(kcpToTcp, tcpToKcp).ConfigureAwait(false);
            try
            {
                await finishedTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Ignore
            }
            catch (ObjectDisposedException)
            {
                // Ignore
            }
            catch (Exception ex)
            {
                Log.LogServerTcpTransportUnhandledException(_logger, ex);
            }
            finally
            {
                cts.CancelAfter(TimeSpan.FromSeconds(10));
            }


            Task unfinishedTask = ReferenceEquals(finishedTask, kcpToTcp) ? tcpToKcp : kcpToTcp;
            try
            {
                await unfinishedTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Ignore
            }
            catch (ObjectDisposedException)
            {
                // Ignore
            }
            catch (Exception ex)
            {
                Log.LogServerTcpTransportUnhandledException(_logger, ex);
            }
        }


        private async Task PumpFromKcpToTcp(CancellationToken cancellationToken)
        {
            bool connectionClosed = false;
            while (true)
            {
                if (!await _conversation.WaitForReceiveQueueAvailableDataAsync(2, cancellationToken).ConfigureAwait(false))
                {
                    return;
                }

                int length = ReadLength(_conversation, out KcpConversationReceiveResult result);
                if (result.TransportClosed)
                {
                    return;
                }

                if (length == 0)
                {
                    // connection closed signal
                    _socket.Dispose();
                    return;
                }

                {
                    using KcpRentedBuffer memoryHandle = _memoryPool.Rent(new KcpBufferPoolRentOptions(length, true));
                    Memory<byte> memory = memoryHandle.Memory.Slice(0, length);

                    // forward data
                    while (!memory.IsEmpty)
                    {
                        result = await _conversation.ReceiveAsync(memory, cancellationToken).ConfigureAwait(false);
                        if (result.TransportClosed)
                        {
                            break;
                        }

                        if (!connectionClosed)
                        {
                            try
                            {
                                await _socket.SendAsync(memory.Slice(0, result.BytesReceived), SocketFlags.None, cancellationToken).ConfigureAwait(false);
                            }
                            catch
                            {
                                connectionClosed = true;
                            }
                        }

                        memory = memory.Slice(result.BytesReceived);
                    }
                }
            }

            static ushort ReadLength(KcpConversation conversation, out KcpConversationReceiveResult result)
            {
                Span<byte> buffer = stackalloc byte[2];
                conversation.TryReceive(buffer, out result);
                return BinaryPrimitives.ReadUInt16LittleEndian(buffer);
            }
        }

        private async Task PumpFromTcpToKcp(CancellationToken cancellationToken)
        {
            using KcpRentedBuffer memoryHandle = _memoryPool.Rent(new KcpBufferPoolRentOptions(ushort.MaxValue, true));
            Memory<byte> buffer = memoryHandle.Memory;

            while (true)
            {
                // receive data
                int bytesReceived;
                try
                {
                    bytesReceived = await _socket.ReceiveAsync(buffer.Slice(0, ushort.MaxValue), SocketFlags.None, cancellationToken).ConfigureAwait(false);
                }
                catch
                {
                    bytesReceived = 0;
                }

                // socket is disposed
                // send connection close signal
                if (bytesReceived == 0)
                {
                    await _conversation.WaitForSendQueueAvailableSpaceAsync(2, 0, cancellationToken).ConfigureAwait(false);
                    if (SendLength(_conversation, 0))
                    {
                        await _conversation.FlushAsync(cancellationToken).ConfigureAwait(false);
                    }
                    return;
                }

                // forward to kcp conversation
                if (!await _conversation.WaitForSendQueueAvailableSpaceAsync(2, 0, cancellationToken).ConfigureAwait(false))
                {
                    return;
                }
                if (!SendLength(_conversation, (ushort)bytesReceived))
                {
                    return;
                }
                if (!await _conversation.SendAsync(buffer.Slice(0, bytesReceived), cancellationToken).ConfigureAwait(false))
                {
                    return;
                }
            }

            static bool SendLength(KcpConversation conversation, ushort length)
            {
                Span<byte> buffer = stackalloc byte[2];
                BinaryPrimitives.WriteUInt16LittleEndian(buffer, length);
                return conversation.TrySend(buffer);
            }
        }
    }
}
