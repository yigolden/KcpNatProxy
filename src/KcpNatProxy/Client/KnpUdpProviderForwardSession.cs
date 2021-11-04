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
    internal sealed class KnpUdpProviderForwardSession : IKnpForwardSession, IThreadPoolWorkItem
    {
        private readonly IKnpForwardHost _host;
        private readonly IKcpBufferPool _bufferPool;
        private readonly int _forwardId;
        private readonly EndPoint _forwardEndPoint;
        private readonly ILogger _logger;

        private readonly object _stateChangeLock = new();
        private Socket? _socket;
        private CancellationTokenSource? _cts;
        private long _lastActiveTimeTick;
        private bool _disposed;

        public KnpUdpProviderForwardSession(IKnpForwardHost host, IKcpBufferPool bufferPool, int forwardId, EndPoint forwardEndPoint, ILogger logger)
        {
            _host = host;
            _bufferPool = bufferPool;
            _forwardId = forwardId;
            _forwardEndPoint = forwardEndPoint;
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
                _socket = new Socket(SocketType.Dgram, ProtocolType.Udp);
                SocketHelper.PatchSocket(_socket);
                Interlocked.Exchange(ref _lastActiveTimeTick, Environment.TickCount64);
                _socket.Connect(_forwardEndPoint);
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
                Socket? socket = _socket;
                if (socket is not null)
                {
                    socket.Dispose();
                    _socket = null;
                }
            }
            _host.NotifySessionClosed(_forwardId, this);
        }

        public bool IsExpired(long tick) => (long)((ulong)tick - (ulong)_lastActiveTimeTick) > 30 * 1000;

        public async ValueTask InputPacketAsync(ReadOnlyMemory<byte> packet, CancellationToken cancellationToken)
        {
            Interlocked.Exchange(ref _lastActiveTimeTick, Environment.TickCount64);
            Socket? socket = Volatile.Read(ref _socket);
            if (socket is null)
            {
                return;
            }
            try
            {
                await socket.SendToAsync(packet, SocketFlags.None, _forwardEndPoint, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (ObjectDisposedException)
            {
                return;
            }
            catch (Exception ex)
            {
                Log.LogServerUnhandledException(_logger, ex);
            }
        }

        async void IThreadPoolWorkItem.Execute()
        {
            int mss = _host.Mss;
            Debug.Assert(mss <= (16 * 1024 - KcpNetworkConnection.PreBufferSize - 8)); // 8: bindingId + forwardId
            using KcpRentedBuffer rentedBuffer = _bufferPool.Rent(new KcpBufferPoolRentOptions(16 * 1024, true)); // 16K

            Socket? socket;
            CancellationToken cancellationToken;
            lock (_stateChangeLock)
            {
                socket = _socket;
                if (socket is null || _cts is null)
                {
                    return;
                }
                cancellationToken = _cts.Token;
            }

            BinaryPrimitives.WriteInt32BigEndian(rentedBuffer.Span, _host.BindingId);
            MemoryMarshal.Write(rentedBuffer.Span.Slice(4), ref Unsafe.AsRef(in _forwardId));

            while (!cancellationToken.IsCancellationRequested)
            {
                SocketReceiveFromResult result;
                try
                {
                    result = await socket.ReceiveFromAsync(rentedBuffer.Memory.Slice(8), SocketFlags.None, _forwardEndPoint, cancellationToken).ConfigureAwait(false);
                    if (result.ReceivedBytes > mss)
                    {
                        continue;
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    Log.LogServerUnhandledException(_logger, ex);
                    continue;
                }

                Interlocked.Exchange(ref _lastActiveTimeTick, Environment.TickCount64);

                try
                {
                    await ForwardBackAsync(rentedBuffer.Memory.Slice(0, 8 + result.ReceivedBytes), cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    Log.LogServerUnhandledException(_logger, ex);
                    continue;
                }
            }

        }

        private ValueTask ForwardBackAsync(ReadOnlyMemory<byte> packet, CancellationToken cancellationToken)
        {
            using var bufferList = KcpRentedBufferList.Allocate(packet);
            return _host.ForwardBackAsync(bufferList, cancellationToken);
        }
    }
}
