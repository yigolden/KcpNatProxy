using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using KcpNatProxy.MemoryPool;
using KcpNatProxy.NetworkConnection;
using KcpSharp;
using Microsoft.Extensions.Logging;

namespace KcpNatProxy.Server
{
    public partial class KnpServer
    {
        private readonly ILogger _logger;
        private readonly uint _serverId;

        private readonly IPEndPoint _endPoint;
        private readonly IPEndPoint _remoteEndPoint;
        private readonly int _mtu;
        private readonly byte[] _password;
        private readonly IKcpBufferPool _bufferPool;

        private readonly KnpInt32IdAllocator _sessionIdAllocator = new();
        private readonly ConcurrentDictionary<int, KnpClientSession> _connections = new();
        private readonly KnpInt32IdAllocator _bindingIdAllocator = new();
        private Dictionary<string, IKnpService>? _services;
        private Dictionary<string, KnpVirtualBusService>? _virtualBuses;

        public IKcpBufferPool BufferPool => _bufferPool;

        internal KnpRentedInt32 AllocateBindingId() => _bindingIdAllocator.Allocate();

        public KnpServer(KnpServerOptions options, ILogger<KnpServer> logger)
        {
            if (options is null)
            {
                throw new ArgumentNullException(nameof(options));
            }

            _logger = logger;
            _serverId = (uint)Random.Shared.Next();

            if (!options.Validate(out string? errorMessage))
            {
                throw new ArgumentException(errorMessage, nameof(options));
            }
            KnpServerListenOptions listenEndPoint = options.Listen;
            if (!listenEndPoint.Validate(out errorMessage, out IPEndPoint? endPoint))
            {
                throw new ArgumentException(errorMessage, nameof(options));
            }
            _endPoint = endPoint;
            _remoteEndPoint = endPoint.AddressFamily == AddressFamily.InterNetwork ? new IPEndPoint(IPAddress.Any, 0) : new IPEndPoint(IPAddress.IPv6Any, 0);
            _mtu = listenEndPoint.Mtu;
            _password = string.IsNullOrEmpty(options.Credential) ? Array.Empty<byte>() : Encoding.UTF8.GetBytes(options.Credential);
            _bufferPool = new PinnedMemoryPool(_mtu);

            foreach (KnpServiceDescription service in options.Services)
            {
                if (service.ServiceType == KnpServiceType.Tcp)
                {
                    if (!service.ValidateTcpUdp(out errorMessage, out IPEndPoint? ipEndPoint))
                    {
                        throw new ArgumentException(errorMessage, nameof(options));
                    }

                    _services ??= new(StringComparer.Ordinal);
                    var parameters = new KnpTcpKcpParameters(service.WindowSize, service.QueueSize, service.UpdateInterval, service.NoDelay);
                    _services.Add(service.Name, new KnpTcpService(service.Name, ipEndPoint, parameters, _logger));
                }
                else if (service.ServiceType == KnpServiceType.Udp)
                {
                    if (!service.ValidateTcpUdp(out errorMessage, out IPEndPoint? ipEndPoint))
                    {
                        throw new ArgumentException(errorMessage, nameof(options));
                    }

                    _services ??= new(StringComparer.Ordinal);
                    _services.Add(service.Name, new KnpUdpService(service.Name, ipEndPoint, _bufferPool, _logger));
                }
                else if (service.ServiceType == KnpServiceType.VirtualBus)
                {
                    if (!service.ValidateVirtualBus(out errorMessage))
                    {
                        throw new ArgumentException(errorMessage, nameof(options));
                    }

                    _virtualBuses ??= new(StringComparer.Ordinal);
                    _virtualBuses.Add(service.Name, new KnpVirtualBusService(service.Name, service.Relay, _logger));
                }
                else
                {
                    throw new ArgumentException($"Listen endpoint is not a valid IP endpoint for service {service.Name}.", nameof(options));
                }
            }

            if (_services is not null)
            {
                foreach (KeyValuePair<string, IKnpService> serviceKv in _services)
                {
                    serviceKv.Value.Start();
                }
            }
        }

        internal KnpServerAuthenticator CreateAuthenticator(int sessionId)
        {
            return new KnpServerAuthenticator(_serverId, _mtu, _password, sessionId);
        }

        internal IKnpService? GetService(string name)
        {
            if (_services is null)
            {
                return null;
            }
            if (_services.TryGetValue(name, out IKnpService? service))
            {
                return service;
            }
            return null;
        }

        internal KnpVirtualBusService? GetVirtualBus(string name)
        {
            if (_virtualBuses is null)
            {
                return null;
            }
            if (_virtualBuses.TryGetValue(name, out KnpVirtualBusService? bus))
            {
                return bus;
            }
            return null;
        }

        public async Task RunAsync(CancellationToken cancellationToken)
        {
            try
            {
                KcpNetworkConnectionListener listener = KcpNetworkConnectionListener.Listen(_endPoint, _remoteEndPoint, 1024, new NetworkConnectionListenerOptions { Mtu = _mtu, BufferPool = _bufferPool });
                listener.SetExceptionHandler((ex, _, state) => Log.LogServerUnhandledException((ILogger?)state!, ex), _logger);

                Log.LogServerListening(_logger, _endPoint);

                await using (listener.ConfigureAwait(false))
                {
                    while (!cancellationToken.IsCancellationRequested)
                    {
                        KcpNetworkConnection networkConnection = await listener.AcceptAsync(cancellationToken).ConfigureAwait(false);
                        var session = new KnpClientSession(this, networkConnection, _sessionIdAllocator.Allocate(), _logger);
                        session.Start();
                    }
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                Log.LogServerListeningFailed(_logger, ex);
            }
        }

        internal bool RegisterClientSession(int sessionId, KnpClientSession session)
        {
            return _connections.TryAdd(sessionId, session);
        }

        internal void UnregisterClientSession(int sessionId, KnpClientSession session)
        {
            if (_connections.TryRemove(new KeyValuePair<int, KnpClientSession>(sessionId, session)))
            {
                session.Dispose();
            }
        }
    }
}
