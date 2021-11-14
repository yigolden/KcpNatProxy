using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using Microsoft.Extensions.Logging;

namespace KcpNatProxy
{
    internal sealed class KnpTcpService : IKnpService, IThreadPoolWorkItem
    {
        private readonly string _name;
        private readonly IPEndPoint _listenEndPoint;
        private readonly KnpTcpKcpParameters _parameters;
        private readonly ILogger _logger;
        private readonly KnpInt32IdAllocator _forwardIdAllocator = new();
        private readonly KnpServiceBindingCollection<KnpTcpServiceBinding> _serviceBindingCollection = new();

        private readonly object _stateChangeLock = new();
        private Socket? _socket;
        private CancellationTokenSource? _cts;
        private bool _disposed;

        public string ServiceName => _name;
        public KnpServiceType ServiceType => KnpServiceType.Tcp;

        public KnpTcpService(string name, IPEndPoint listenEndPoint, KnpTcpKcpParameters parameters, ILogger logger)
        {
            _name = name;
            _listenEndPoint = listenEndPoint;
            _parameters = parameters;
            _logger = logger;
        }

        public int WriteParameters(Span<byte> buffer)
        {
            _parameters.TrySerialize(buffer);
            return 4;
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
                _socket = new Socket(SocketType.Stream, ProtocolType.Tcp);
                _socket.Bind(_listenEndPoint);
                _socket.Listen();
            }
            ThreadPool.UnsafeQueueUserWorkItem(this, false);
            Log.LogCommonTcpServiceStarted(_logger, _name, _listenEndPoint);
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
            _serviceBindingCollection.Dispose();
        }

        public IKnpServiceBinding? CreateBinding(IKnpConnectionHost host, KnpRentedInt32 bindingId, ReadOnlySpan<byte> parameters)
        {
            if (_disposed)
            {
                bindingId.Dispose();
                return null;
            }

            _ = KnpTcpKcpRemoteParameters.TryParse(parameters, out KnpTcpKcpRemoteParameters remoteParameters);

            var serviceBinding = new KnpTcpServiceBinding(_name, host, bindingId, _serviceBindingCollection, _parameters, remoteParameters);
            _serviceBindingCollection.Add(serviceBinding);

            if (_disposed)
            {
                serviceBinding.Dispose();
                return null;
            }

            return serviceBinding;
        }

        async void IThreadPoolWorkItem.Execute()
        {
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

            while (!cancellationToken.IsCancellationRequested)
            {
                Socket acceptedSocket;
                try
                {
                    Log.LogCommonTcpConnectionAccepting(_logger, _name);
                    acceptedSocket = await socket.AcceptAsync(cancellationToken).ConfigureAwait(false);
                    acceptedSocket.NoDelay = true;
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    Log.LogServerUnhandledException(_logger, ex);
                    break;
                }

                Log.LogCommonTcpConnectionAccepted(_logger, _name, acceptedSocket.RemoteEndPoint);
                KnpTcpServiceBinding? serviceBinding = _serviceBindingCollection.GetAvailableBinding();
                if (serviceBinding is null)
                {
                    Log.LogCommonServiceBindingQueryNotFound(_logger, _name);
                    acceptedSocket.Dispose();
                    continue;
                }
                if (_disposed)
                {
                    serviceBinding.Dispose();
                    break;
                }

                Log.LogCommonServiceBindingQueryFound(_logger, _name, serviceBinding.BindingId);
                serviceBinding.Run(acceptedSocket, _forwardIdAllocator.Allocate());
            }
        }
    }
}
