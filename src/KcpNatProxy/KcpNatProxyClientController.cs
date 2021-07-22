using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using KcpSharp;
using Microsoft.Extensions.Logging;

namespace KcpNatProxy
{
    internal sealed partial class KcpNatProxyClientController : IKcpTransport, IDisposable
    {
        private readonly Socket _socket;
        private readonly UdpSocketSendQueue _sendQueue;
        private readonly EndPoint _remoteEndPoint;
        private readonly byte[]? _password;
        private readonly IReadOnlyList<KcpNatProxyServiceDescriptor> _services;
        private readonly int _mtu;
        private readonly MemoryPool _memoryPool;
        private readonly ILogger _logger;
        private CancellationTokenSource? _cts;

        private KcpConversation? _authConversation;
        private KcpConversation? _controlConversation;
        private KcpNatProxyClientWorkerDispatcher? _dispatcher;
        private Guid? _sessionId;
        private DateTime _lastHeartbeatTime;

        public KcpNatProxyClientController(Socket socket, EndPoint remoteEndPoint, byte[]? password, IReadOnlyList<KcpNatProxyServiceDescriptor> services, int mtu, MemoryPool memoryPool, ILogger logger)
        {
            _socket = socket;
            _sendQueue = new UdpSocketSendQueue(socket, 1024);
            _remoteEndPoint = remoteEndPoint;
            _password = password;
            _services = services;
            _mtu = mtu;
            _memoryPool = memoryPool;
            _logger = logger;
        }

        public async Task<TimeSpan> RunAsync(CancellationToken cancellationToken)
        {
            CancellationToken originalCancellationToken = cancellationToken;
            using CancellationTokenSource cts = _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cancellationToken = cts.Token;
            _ = Task.Run(() => ReceiveLoop(_cts.Token), cancellationToken);

            try
            {
                int mtu = _mtu;
                // authenticate
                _authConversation = new KcpConversation(this, 0, new KcpConversationOptions { PreBufferSize = 4, BufferPool = _memoryPool, Mtu = 256 });
                try
                {
                    Log.LogClientAuthenticationStart(_logger);
                    var context = new KcpNatProxyClientAuthenticationContext(_authConversation, _password, mtu, _logger);
                    _sessionId = await context.AuthenticateAsync(cancellationToken).ConfigureAwait(false);
                    if (!_sessionId.HasValue)
                    {
                        return TimeSpan.FromSeconds(30);
                    }
                    mtu = context.NegotiatedMtu;
                    Log.LogClientAuthenticationSuccess(_logger, mtu);
                }
                finally
                {
                    _authConversation.Dispose();
                    _authConversation = null;
                }

                // start heartbeat loop
                Task sendHeartbeatLoop = Task.Run(() => SendHeartbeatLoop(cancellationToken), cancellationToken);

                // create control channel and send commands
                _controlConversation = new KcpConversation(this, 2, new KcpConversationOptions { PreBufferSize = 4, BufferPool = _memoryPool, Mtu = mtu });
                _dispatcher = new KcpNatProxyClientWorkerDispatcher(this, _mtu, _memoryPool, _logger);
                try
                {
                    await _dispatcher.RunAsync(_controlConversation, _services, cancellationToken).ConfigureAwait(false);
                }
                finally
                {
                    _controlConversation.Dispose();
                    _controlConversation = null;
                }

                await sendHeartbeatLoop.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                if (originalCancellationToken.IsCancellationRequested)
                {
                    throw;
                }
            }

            return TimeSpan.FromSeconds(30);
        }

        public ValueTask SendPacketAsync(Memory<byte> packet, CancellationToken cancellationToken)
            => _sendQueue.SendAsync(_remoteEndPoint, packet, cancellationToken);

        private async Task ReceiveLoop(CancellationToken cancellationToken)
        {
            using KcpRentedBuffer memoryHandle = _memoryPool.Rent(new KcpBufferPoolRentOptions(_mtu, true));
            Memory<byte> buffer = memoryHandle.Memory.Slice(0, _mtu);
            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    SocketReceiveFromResult result = await _socket.ReceiveFromAsync(buffer, SocketFlags.None, _remoteEndPoint, cancellationToken).ConfigureAwait(false);
                    if (result.ReceivedBytes < 8)
                    {
                        continue;
                    }
                    if (!BinaryPrimitives.TryReadInt32LittleEndian(buffer.Span, out int serviceId))
                    {
                        continue;
                    }
                    if (serviceId == 0)
                    {
                        if (!BinaryPrimitives.TryReadInt32LittleEndian(buffer.Span.Slice(4), out int channelId))
                        {
                            continue;
                        }
                        if (channelId == 0)
                        {
                            KcpConversation? authConversation = _authConversation;
                            if (authConversation is not null)
                            {
                                await authConversation.InputPakcetAsync(buffer.Slice(4, result.ReceivedBytes - 4), cancellationToken).ConfigureAwait(false);
                                continue;
                            }
                        }
                        if (channelId == 1)
                        {
                            await ProcessHeartbeatResponse(buffer.Slice(8, result.ReceivedBytes - 8), cancellationToken).ConfigureAwait(false);
                            continue;
                        }
                        if (channelId == 2)
                        {
                            KcpConversation? controlConversation = _controlConversation;
                            if (controlConversation is not null)
                            {
                                await controlConversation.InputPakcetAsync(buffer.Slice(4, result.ReceivedBytes - 4), cancellationToken).ConfigureAwait(false);
                                continue;
                            }
                        }
                        continue;
                    }

                    // dispatch message
                    KcpNatProxyClientWorkerDispatcher? dispatcher = _dispatcher;
                    if (dispatcher is not null)
                    {
                        await dispatcher.InputPacketAsync(serviceId, buffer.Slice(4, result.ReceivedBytes - 4), cancellationToken).ConfigureAwait(false);
                    }
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
            catch (SocketException ex)
            {
                Log.LogClientUnhandledSocketExceptionWhenReceiving(_logger, ex);
                CancelCurrentOperation();
            }
            catch (Exception ex)
            {
                Log.LogClientUnhandledExceptionWhenReceiving(_logger, ex);
                CancelCurrentOperation();
            }
        }

        private async Task SendHeartbeatLoop(CancellationToken cancellationToken)
        {
            Guid sessionId = _sessionId.GetValueOrDefault();
            _lastHeartbeatTime = DateTime.UtcNow;
            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    await Task.Delay(TimeSpan.FromSeconds(20), cancellationToken).ConfigureAwait(false);
                    {
                        using KcpRentedBuffer memoryHandle = _memoryPool.Rent(new KcpBufferPoolRentOptions(24, true));
                        Memory<byte> buffer = memoryHandle.Memory;
                        BinaryPrimitives.WriteInt32LittleEndian(buffer.Span, 0);
                        BinaryPrimitives.WriteInt32LittleEndian(buffer.Span.Slice(4), 1);
                        sessionId.TryWriteBytes(buffer.Span.Slice(8));
                        await _sendQueue.SendAsync(_remoteEndPoint, buffer.Slice(0, 24), cancellationToken).ConfigureAwait(false);
                    }
                    if (DateTime.UtcNow.AddSeconds(-80) > _lastHeartbeatTime)
                    {
                        Log.LogClientTooLongNoHeartbeat(_logger);
                        CancelCurrentOperation();
                        return;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                using KcpRentedBuffer memoryHandle = _memoryPool.Rent(new KcpBufferPoolRentOptions(24, true));
                Memory<byte> buffer = memoryHandle.Memory;
                BinaryPrimitives.WriteInt32LittleEndian(buffer.Span, 0);
                BinaryPrimitives.WriteInt32LittleEndian(buffer.Span.Slice(4), 1);
                buffer.Span.Slice(8, 16).Clear();
                await _sendQueue.SendAsync(_remoteEndPoint, buffer.Slice(0, 24), cts.Token).ConfigureAwait(false);
            }
            catch (SocketException ex)
            {
                Log.LogClientUnhandledSocketExceptionWhenSendingHeartbeat(_logger, ex);
                CancelCurrentOperation();
            }
            catch(Exception ex)
            {
                Log.LogClientUnhandledExceptionWhenSendingHeartbeat(_logger, ex);
                CancelCurrentOperation();
            }
        }

        private ValueTask ProcessHeartbeatResponse(ReadOnlyMemory<byte> content, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!_sessionId.HasValue)
            {
                return default;
            }
            if (content.Length < 16)
            {
                return default;
            }
            Guid receivedGuid = new Guid(content.Span.Slice(0, 16));
            if (receivedGuid != _sessionId.GetValueOrDefault())
            {
                Log.LogClientReceivedInvalidHeartbeat(_logger);
                CancelCurrentOperation();
                return default;
            }
            _lastHeartbeatTime = DateTime.UtcNow;
            return default;
        }

        public void CancelCurrentOperation()
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

        public void Dispose()
        {
            CancelCurrentOperation();
            _sendQueue.Dispose();
        }
    }
}
