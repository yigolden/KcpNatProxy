using System;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using KcpNatProxy.NetworkConnection;
using KcpSharp;
using Microsoft.Extensions.Logging;

namespace KcpNatProxy.Client
{
    internal partial class KnpTcpProviderForwardSession : IKnpForwardSession, IKcpTransport, IThreadPoolWorkItem
    {
        private readonly IKnpForwardHost _host;
        private readonly IKcpBufferPool _bufferPool;
        private readonly int _forwardId;
        private readonly EndPoint _forwardEndPoint;
        private readonly KnpTcpKcpParameters _parameters;
        private readonly KnpTcpKcpRemoteParameters _remoteParameters;
        private readonly ILogger _logger;

        private readonly object _stateChangeLock = new();
        private byte[]? _bindingForwardIdBytes;
        private Socket? _socket;
        private KcpConversation? _forwardChannel;
        private CancellationTokenSource? _cts;
        private bool _disposed;

        public KnpTcpProviderForwardSession(IKnpForwardHost host, IKcpBufferPool bufferPool, int forwardId, EndPoint forwardEndPoint, KnpTcpKcpParameters parameters, KnpTcpKcpRemoteParameters remoteParameters, ILogger logger)
        {
            _host = host;
            _bufferPool = bufferPool;
            _forwardId = forwardId;
            _forwardEndPoint = forwardEndPoint;
            _parameters = parameters;
            _remoteParameters = remoteParameters;
            _logger = logger;
        }

        public void Start()
        {
            int mss = _host.Mss;
            Debug.Assert(mss <= (16 * 1024 - KcpNetworkConnection.PreBufferSize - 8)); // 8: bindingId + forwardId

            lock (_stateChangeLock)
            {
                if (_disposed || _cts is not null)
                {
                    return;
                }

                _cts = new CancellationTokenSource();
                _socket = new Socket(SocketType.Stream, ProtocolType.Tcp);
                _socket.NoDelay = _parameters.NoDelay & _remoteParameters.NoDelay;
                _forwardChannel = new KcpConversation(this, new KcpConversationOptions
                {
                    BufferPool = _bufferPool,
                    Mtu = mss,
                    StreamMode = true,
                    UpdateInterval = Math.Max(_parameters.UpdateInterval, _remoteParameters.UpdateInterval),
                    SendWindow = _parameters.WindowSize,
                    ReceiveWindow = Math.Min(_parameters.WindowSize, _remoteParameters.WindowSize),
                    RemoteReceiveWindow = _remoteParameters.WindowSize,
                    SendQueueSize = _parameters.QueueSize,
                    NoDelay = _parameters.NoDelay & _remoteParameters.NoDelay
                });
                _bindingForwardIdBytes = new byte[8];
                BinaryPrimitives.WriteInt32BigEndian(_bindingForwardIdBytes, _host.BindingId);
                MemoryMarshal.Write(_bindingForwardIdBytes.AsSpan(4), ref Unsafe.AsRef(in _forwardId));
            }
            ThreadPool.UnsafeQueueUserWorkItem(this, false);
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
                CancellationTokenSource? cts = _cts;
                if (cts is not null)
                {
                    cts.Cancel();
                    cts.Dispose();
                    _cts = null;
                }
                KcpConversation? forwardChannel = _forwardChannel;
                if (forwardChannel is not null)
                {
                    forwardChannel.Dispose();
                    _forwardChannel = null;
                }
                Socket? socket = _socket;
                if (socket is not null)
                {
                    socket.Dispose();
                    _socket = null;
                }
                _bindingForwardIdBytes = null;
            }
            _host.NotifySessionClosed(_forwardId, this);
        }

        public bool IsExpired(long tick) => _disposed;

        public ValueTask InputPacketAsync(ReadOnlyMemory<byte> packet, CancellationToken cancellationToken)
        {
            KcpConversation? forwardChannel = Volatile.Read(ref _forwardChannel);
            if (forwardChannel is null)
            {
                return default;
            }
            return forwardChannel.InputPakcetAsync(packet, cancellationToken);
        }

        ValueTask IKcpTransport.SendPacketAsync(Memory<byte> packet, CancellationToken cancellationToken)
        {
            byte[]? bindingForwardIdBytes = Volatile.Read(ref _bindingForwardIdBytes);
            if (bindingForwardIdBytes is null)
            {
                return default;
            }

            using var bufferList = KcpRentedBufferList.Allocate(packet);
            return _host.ForwardBackAsync(bufferList.AddPreBuffer(bindingForwardIdBytes), cancellationToken);
        }

        async void IThreadPoolWorkItem.Execute()
        {
            Socket? socket;
            KcpConversation? forwardChannel;
            CancellationToken cancellationToken;
            lock (_stateChangeLock)
            {
                socket = _socket;
                forwardChannel = _forwardChannel;
                if (socket is null || forwardChannel is null || _cts is null)
                {
                    return;
                }
                cancellationToken = _cts.Token;
            }

            if (cancellationToken.IsCancellationRequested)
            {
                return;
            }

            try
            {
                // connect to remote host
                {
                    Unsafe.SkipInit(out byte b);
                    using var timeoutToken = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    timeoutToken.CancelAfter(TimeSpan.FromSeconds(15));
                    KcpConversationReceiveResult result = await forwardChannel.WaitToReceiveAsync(timeoutToken.Token).ConfigureAwait(false);
                    if (result.TransportClosed)
                    {
                        return;
                    }
                    if (!forwardChannel.TryReceive(MemoryMarshal.CreateSpan(ref b, 1), out result) || b != 0)
                    {
                        return;
                    }
                    try
                    {
                        await socket.ConnectAsync(_forwardEndPoint, timeoutToken.Token).ConfigureAwait(false);
                        b = 0;
                        forwardChannel.TrySend(MemoryMarshal.CreateSpan(ref b, 1));
                    }
                    catch (Exception ex)
                    {
                        if (!cancellationToken.IsCancellationRequested)
                        {
                            if (timeoutToken.IsCancellationRequested)
                            {
                                LogClientTcpConnectTimeout(_logger);
                            }
                            else
                            {
                                LogClientTcpConnectFailed(_logger, ex);
                            }
                        }
                        using var replyTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                        replyTimeout.CancelAfter(TimeSpan.FromSeconds(10));
                        b = 1;
                        forwardChannel.TrySend(MemoryMarshal.CreateSpan(ref b, 1));
                        await forwardChannel.FlushAsync(replyTimeout.Token).ConfigureAwait(false);
                        return;
                    }
                }

                // connection is established
                {
                    var exchange = new KcpTcpDataExchange(socket, forwardChannel, _bufferPool, _logger);
                    await exchange.RunAsync(cancellationToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                Log.LogClientUnhandledException(_logger, ex);
            }
            finally
            {
                Dispose();
            }
        }

        [LoggerMessage(EventId = 0xE002, Level = LogLevel.Debug, Message = "Failed to connect to the forward end point. The connect operation timed out.")]
        private static partial void LogClientTcpConnectTimeout(ILogger logger);

        [LoggerMessage(EventId = 0xE003, Level = LogLevel.Debug, Message = "Failed to connect to the forward end point. The connect operation timed out.")]
        private static partial void LogClientTcpConnectFailed(ILogger logger, Exception ex);

    }
}
