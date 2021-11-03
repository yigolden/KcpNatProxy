using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using KcpNatProxy.SocketTransport;
using KcpSharp;
using Microsoft.Extensions.Logging;

namespace KcpNatProxy
{
    internal sealed class KnpUdpService : IKnpService, IKnpServiceBindingHolder, IThreadPoolWorkItem
    {
        private readonly string _name;
        private readonly IPEndPoint _listenEndPoint;
        private readonly IKcpBufferPool _bufferPool;
        private readonly ILogger _logger;
        private readonly KnpInt32IdAllocator _forwardIdAllocator = new();
        private readonly KnpServiceBindingCollection<KnpUdpServiceBinding> _serviceBindingCollection = new();

        private readonly object _stateChangeLock = new();
        private Socket? _socket;
        private KcpSocketNetworkSendQueue? _sendQueue;
        private CancellationTokenSource? _cts;
        private bool _disposed;

        private readonly ConcurrentDictionary<EndPoint, KnpUdpServiceForwardSession> _sourceForwardBindings = new();

        public string ServiceName => _name;
        public KnpServiceType ServiceType => KnpServiceType.Udp;

        public KnpUdpService(string name, IPEndPoint listenEndPoint, IKcpBufferPool bufferPool, ILogger logger)
        {
            _name = name;
            _listenEndPoint = listenEndPoint;
            _bufferPool = bufferPool;
            _logger = logger;
        }

        public int WriteParameters(Span<byte> buffer) => 0;

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
                _socket.Bind(_listenEndPoint);
                _sendQueue = new KcpSocketNetworkSendQueue(_bufferPool, _socket, 1024);
            }
            ThreadPool.UnsafeQueueUserWorkItem(this, false);
            Log.LogCommonUdpServiceStarted(_logger, _name, _listenEndPoint);
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
                KcpSocketNetworkSendQueue? sendQueue = _sendQueue;
                if (sendQueue is not null)
                {
                    sendQueue.Dispose();
                    _sendQueue = null;
                }
                Socket? socket = _socket;
                if (socket is not null)
                {
                    socket.Dispose();
                    _socket = null;
                }
            }
            _serviceBindingCollection.Dispose();
            KnpCollectionHelper.ClearAndDispose(_sourceForwardBindings);
        }

        public IKnpServiceBinding? CreateBinding(IKnpConnectionHost host, KnpRentedInt32 bindingId, ReadOnlySpan<byte> parameters)
        {
            if (_disposed)
            {
                bindingId.Dispose();
                return null;
            }

            var serviceBinding = new KnpUdpServiceBinding(this, host, bindingId);
            _serviceBindingCollection.Add(serviceBinding);

            if (_disposed)
            {
                serviceBinding.Dispose();
                return null;
            }

            return serviceBinding;
        }

        void IKnpServiceBindingHolder.Remove(IKnpServiceBinding serviceBinding)
        {
            _serviceBindingCollection.Remove(serviceBinding);

            if (_disposed)
            {
                return;
            }

            foreach (KeyValuePair<EndPoint, KnpUdpServiceForwardSession> item in _sourceForwardBindings)
            {
                if (ReferenceEquals(item.Value.ServiceBinding, serviceBinding))
                {
                    _ = _sourceForwardBindings.TryRemove(item);
                }
            }
        }

        async void IThreadPoolWorkItem.Execute()
        {
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

            var remoteEndPoint = new IPEndPoint(_listenEndPoint.AddressFamily == AddressFamily.InterNetwork ? IPAddress.Any : IPAddress.IPv6Any, 0);
            while (!cancellationToken.IsCancellationRequested)
            {
                SocketReceiveFromResult result;
                try
                {
                    result = await socket.ReceiveFromAsync(rentedBuffer.Memory.Slice(8), SocketFlags.None, remoteEndPoint, cancellationToken).ConfigureAwait(false);
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

                Log.LogCommonUdpPacketReceived(_logger, _name, result.RemoteEndPoint, result.ReceivedBytes);
                if (result.ReceivedBytes != 0)
                {
                    await ForwardAsync(rentedBuffer.Memory.Slice(0, 8 + result.ReceivedBytes), result.RemoteEndPoint, cancellationToken).ConfigureAwait(false);
                }
            }
        }

        internal void RemoveEndPoint(KnpUdpServiceForwardSession forwardSession)
        {
            _sourceForwardBindings.TryRemove(new KeyValuePair<EndPoint, KnpUdpServiceForwardSession>(forwardSession.EndPoint, forwardSession));
        }

        internal ValueTask SendPakcetAsync(ReadOnlyMemory<byte> packet, EndPoint endPoint, CancellationToken cancellationToken)
        {
            KcpSocketNetworkSendQueue? sendQueue = Volatile.Read(ref _sendQueue);
            if (sendQueue is null)
            {
                return default;
            }

            return sendQueue.SendAsync(packet, endPoint, cancellationToken);
        }

        private ValueTask ForwardAsync(Memory<byte> buffer, EndPoint remoteEndPoint, CancellationToken cancellationToken)
        {
            if (_disposed)
            {
                return default;
            }

            KnpUdpServiceBinding? serviceBinding;
            KnpUdpServiceForwardSession? forwardSession;
            if (_sourceForwardBindings.TryGetValue(remoteEndPoint, out forwardSession))
            {
                serviceBinding = forwardSession.ServiceBinding;
                if ((buffer.Length - 8) > serviceBinding.Mss)
                {
                    return default;
                }

                return forwardSession.ForwardAsync(buffer, cancellationToken);
            }

            serviceBinding = _serviceBindingCollection.GetAvailableBinding();
            if (serviceBinding is null)
            {
                Log.LogCommonServiceBindingQueryNotFound(_logger, _name);
                return default;
            }

            Log.LogCommonServiceBindingQueryFound(_logger, _name, serviceBinding.BindingId);
            forwardSession = serviceBinding.CreateForwardSession(_forwardIdAllocator.Allocate(), remoteEndPoint, buffer.Length - 8);
            if (forwardSession is null)
            {
                return default;
            }

            if (!_sourceForwardBindings.TryAdd(remoteEndPoint, forwardSession))
            {
                forwardSession.Dispose();
                return default;
            }
            if (_disposed)
            {
                forwardSession.Dispose();
                return default;
            }

            return forwardSession.ForwardAsync(buffer, cancellationToken);
        }


    }
}
