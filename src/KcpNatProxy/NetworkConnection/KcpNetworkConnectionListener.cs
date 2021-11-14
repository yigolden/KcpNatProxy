using System;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using KcpNatProxy.SocketTransport;
using KcpSharp;

namespace KcpNatProxy.NetworkConnection
{
    public sealed class KcpNetworkConnectionListener : IKcpNetworkApplication, IKcpExceptionProducer<KcpNetworkConnectionListener>, IAsyncDisposable, IDisposable
    {
        private KcpSocketNetworkTransport? _transport;
        private bool _ownsTransport;
        private readonly KcpNetworkConnectionOptions _connectionOptions;

        private KcpNetworkConnectionAcceptQueue? _acceptQueue;
        private bool _transportClosed;
        private bool _disposed;

        private KcpExceptionProducerCore<KcpNetworkConnectionListener> _exceptionProducer;

        public KcpNetworkConnectionListener(KcpSocketNetworkTransport transport, NetworkConnectionListenerOptions? options)
        {
            _transport = transport;
            _ownsTransport = false;
            _connectionOptions = new KcpNetworkConnectionOptions
            {
                BufferPool = options?.BufferPool,
                Mtu = options?.Mtu ?? 1400
            };
            _acceptQueue = new KcpNetworkConnectionAcceptQueue(options?.BackLog ?? 128);

            _transport.SetExceptionHandler((ex, _, state) =>
            {
                var thisObject = (KcpNetworkConnectionListener?)state!;
                thisObject._exceptionProducer.RaiseException(this, ex);
            }, this);
        }

        internal KcpNetworkConnectionListener(KcpSocketNetworkTransport transport, bool ownsTransport, NetworkConnectionListenerOptions? options)
        {
            _transport = transport;
            _ownsTransport = ownsTransport;
            _connectionOptions = new KcpNetworkConnectionOptions
            {
                BufferPool = options?.BufferPool,
                Mtu = options?.Mtu ?? 1400
            };
            _acceptQueue = new KcpNetworkConnectionAcceptQueue(options?.BackLog ?? 128);

            _transport.SetExceptionHandler((ex, source, state) =>
            {
                var thisObject = (KcpNetworkConnectionListener?)state!;
                thisObject._exceptionProducer.RaiseException(this, ex);
            }, this);
        }

        public static KcpNetworkConnectionListener Listen(EndPoint localEndPoint, EndPoint remoteEndPoint, int sendQueueSize, NetworkConnectionListenerOptions? options)
        {
            KcpSocketNetworkTransport? transport = new KcpSocketNetworkTransport(options?.Mtu ?? 1400, options?.BufferPool);
            KcpNetworkConnectionListener? listener = null;
            try
            {
                transport.Bind(localEndPoint);

                listener = new KcpNetworkConnectionListener(transport, true, options);

                transport.RegisterFallback(listener);

                transport.Start(remoteEndPoint, sendQueueSize);

                transport = null;
                return Interlocked.Exchange<KcpNetworkConnectionListener?>(ref listener, null);
            }
            finally
            {
#pragma warning disable CA1508 // Avoid dead conditional code
                listener?.Dispose();
                transport?.Dispose();
#pragma warning restore CA1508 // Avoid dead conditional code
            }
        }

        public void SetExceptionHandler(Func<Exception, KcpNetworkConnectionListener, object?, bool> handler, object? state)
            => _exceptionProducer.SetExceptionHandler(handler, state);

        public ValueTask<KcpNetworkConnection> AcceptAsync(CancellationToken cancellationToken = default)
        {
            KcpNetworkConnectionAcceptQueue? acceptQueue = Volatile.Read(ref _acceptQueue);
            if (acceptQueue is null)
            {
                return ValueTask.FromException<KcpNetworkConnection>(new ObjectDisposedException(nameof(KcpNetworkConnectionListener)));
            }
            return acceptQueue.AcceptAsync(cancellationToken);
        }

        ValueTask IKcpNetworkApplication.InputPacketAsync(ReadOnlyMemory<byte> packet, EndPoint remoteEndPoint, CancellationToken cancellationToken)
        {
            if (!ValidatePacket(packet.Span))
            {
                return default;
            }

            if (_transportClosed || _disposed)
            {
                return default;
            }

            IKcpNetworkTransport? transport = Volatile.Read(ref _transport);
            if (transport is null)
            {
                return default;
            }

            KcpNetworkConnectionAcceptQueue? acceptQueue = Volatile.Read(ref _acceptQueue);
            if (acceptQueue is null || !acceptQueue.IsQueueAvailable())
            {
                return default;
            }

            var networkConnection = new KcpNetworkConnection(transport, false, remoteEndPoint, _connectionOptions);

            try
            {
                KcpSocketNetworkApplicationRegistration registration = transport.Register(remoteEndPoint, networkConnection);
                networkConnection.SetApplicationRegistration(registration);
            }
            catch
            {
                networkConnection.Dispose();
                return default;
            }

            if (!acceptQueue.TryQueue(networkConnection))
            {
                networkConnection.Dispose();
                return default;
            }

            return ((IKcpNetworkApplication)networkConnection).InputPacketAsync(packet, remoteEndPoint, cancellationToken);
        }

        private static bool ValidatePacket(ReadOnlySpan<byte> packet)
        {
            if (packet.Length < 8)
            {
                return false;
            }
            if (packet[0] != 0x01)
            {
                return false;
            }
            if ((packet[1] & 0b11111100) != 0b10000000)
            {
                return false;
            }
            if (packet[4] != 0 || packet[5] != 0 || packet[6] != 0)
            {
                return false;
            }
            return true;
        }

        public void SetTransportClosed()
        {
            if (_transportClosed)
            {
                return;
            }
            _transportClosed = true;

            KcpNetworkConnectionAcceptQueue? acceptQueue = Interlocked.Exchange(ref _acceptQueue, null);
            if (acceptQueue is not null)
            {
                acceptQueue.Dispose();
            }
        }

        public ValueTask SetTransportClosedAsync()
        {
            SetTransportClosed();
            return default;
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;

            SetTransportClosed();

            IKcpNetworkTransport? transport = Interlocked.Exchange(ref _transport, null);
            if (transport is not null && _ownsTransport)
            {
                _ownsTransport = false;
                transport.Dispose();
            }

            _exceptionProducer.Clear();
        }

        public async ValueTask DisposeAsync()
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;

            SetTransportClosed();

            IKcpNetworkTransport? transport = Interlocked.Exchange(ref _transport, null);
            if (transport is not null && _ownsTransport)
            {
                _ownsTransport = false;
                await transport.DisposeAsync().ConfigureAwait(false);
            }

            _exceptionProducer.Clear();
        }

    }
}
