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
    internal sealed class KnpTcpServiceForwardSession : IKnpForwardSession, IKcpTransport, IThreadPoolWorkItem
    {
        private readonly IKnpForwardHost _host;
        private readonly IKcpBufferPool _bufferPool;
        private readonly KnpRentedInt32 _forwardId;
        private readonly Socket _socket;
        private readonly KnpTcpKcpParameters _parameters;
        private readonly KnpTcpKcpRemoteParameters _remoteParameters;
        private readonly ILogger _logger;

        private readonly object _stateChangeLock = new();
        private CancellationTokenSource? _cts;
        private KcpConversation? _forwardChannel;
        private byte[]? _bindingForwardIdBytes;
        private bool _disposed;

        public KnpTcpServiceForwardSession(IKnpForwardHost host, IKcpBufferPool bufferPool, KnpRentedInt32 forwardId, Socket socket, KnpTcpKcpParameters parameters, KnpTcpKcpRemoteParameters remoteParameters, ILogger logger)
        {
            _host = host;
            _bufferPool = bufferPool;
            _forwardId = forwardId;
            _socket = socket;
            _parameters = parameters;
            _remoteParameters = remoteParameters;
            _logger = logger;
        }

        public void Start()
        {
            lock (_stateChangeLock)
            {
                if (_disposed || _cts is not null)
                {
                    return;
                }

                _cts = new CancellationTokenSource();
                _socket.NoDelay = _parameters.NoDelay & _remoteParameters.NoDelay;
                _forwardChannel = new KcpConversation(this, new KcpConversationOptions
                {
                    BufferPool = _bufferPool,
                    Mtu = _host.Mss,
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
                _forwardId.Write(_bindingForwardIdBytes.AsSpan(4));
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

                _bindingForwardIdBytes = null;
            }

            _socket.Dispose();
            _forwardId.Dispose();

            _host.NotifySessionClosed(_forwardId.Value, this);
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
            KcpConversation? forwardChannel;
            CancellationToken cancellationToken;
            lock (_stateChangeLock)
            {
                forwardChannel = _forwardChannel;
                if (forwardChannel is null || _cts is null)
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
                // connect phase
                {
                    byte b = 0;
                    using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    cts.CancelAfter(TimeSpan.FromSeconds(20));
                    forwardChannel.TrySend(MemoryMarshal.CreateSpan(ref b, 1));
                    await forwardChannel.WaitToReceiveAsync(cts.Token).ConfigureAwait(false);
                    if (!forwardChannel.TryReceive(MemoryMarshal.CreateSpan(ref b, 1), out _))
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
                    var exchange = new KcpTcpDataExchange(_socket, forwardChannel, _bufferPool, _logger);
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

    }
}
