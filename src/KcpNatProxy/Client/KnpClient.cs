using System;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using KcpNatProxy.MemoryPool;
using KcpNatProxy.NetworkConnection;
using KcpNatProxy.SocketTransport;
using KcpSharp;
using Microsoft.Extensions.Logging;

namespace KcpNatProxy.Client
{
    public sealed class KnpClient : IKcpNetworkConnectionCallback, IKnpConnectionHost, IKcpTransport, IThreadPoolWorkItem
    {
        private readonly ILogger _logger;

        private readonly EndPoint _endPoint;
        private readonly int _mtu;
        private readonly byte[] _password;
        private readonly KnpProviderFactory[] _factories;
        private readonly IKcpBufferPool _bufferPool;

        private KcpNetworkConnection? _connection;
        private KcpConversation? _controlChannel;
        private TaskCompletionSource? _connectionClosed;
        private CancellationTokenSource? _connectionClosedCts;

        private readonly KnpVirtualBusCollection? _virtualBusCollection;
        private readonly IKnpService[]? _services;
        private readonly ConcurrentDictionary<int, IKnpProvider> _providers = new();
        private readonly KnpPeerConnectionReceiver? _peerConnectionReceiver;

        private readonly ConcurrentDictionary<long, IKnpForwardSession> _forwardSessions = new();

        IKcpBufferPool IKnpConnectionHost.BufferPool => _bufferPool;
        ILogger IKnpConnectionHost.Logger => _logger;
        int IKnpConnectionHost.Mtu => _connection is null ? (_mtu - KcpNetworkConnection.PreBufferSize) : _connection.Mss;
        EndPoint? IKnpConnectionHost.RemoteEndPoint => _connection?.RemoteEndPoint;

        public bool TryGetSessionId(out int sessionId, [NotNullWhen(true)] out byte[]? sessionIdBytes)
        {
            sessionId = default;
            sessionIdBytes = default;
            return false;
        }

        public KnpClient(KnpClientOptions options, ILogger<KnpClient> logger)
        {
            if (options is null)
            {
                throw new ArgumentNullException(nameof(options));
            }

            _logger = logger;
            if (!options.Validate(out string? errorMessage))
            {
                throw new ArgumentException(errorMessage, nameof(options));
            }
            KnpClientConnectOptions connectEndPoint = options.Connect[0];
            if (!connectEndPoint.Validate(out errorMessage, out EndPoint? endPoint))
            {
                throw new ArgumentException(errorMessage, nameof(options));
            }
            _endPoint = endPoint;
            _mtu = connectEndPoint.Mtu;
            _password = string.IsNullOrEmpty(options.Credential) ? Array.Empty<byte>() : Encoding.UTF8.GetBytes(options.Credential);

            _bufferPool = new PinnedMemoryPool(_mtu);

            var peerConnectionReceiver = new KnpPeerConnectionReceiver(_mtu, _bufferPool);
            var virtualBusCollection = new KnpVirtualBusCollection(_mtu, peerConnectionReceiver, _bufferPool, logger);

            // prepare all services
            var services = new List<IKnpService>();
            if (options.Services is not null)
            {
                foreach (KnpServiceDescription? service in options.Services)
                {
                    if (!service.Validate(out errorMessage, out IPEndPoint? listenEndPoint))
                    {
                        throw new ArgumentException(errorMessage, nameof(options));
                    }
                    KnpVirtualBusLifetime virtualBus = virtualBusCollection.GetOrCreate(service.VirtualBus);

                    if (service.ServiceType == KnpServiceType.Udp)
                    {
                        var udpService = new KnpUdpService(service.Name, listenEndPoint, _bufferPool, _logger);
                        virtualBus.AddService(udpService);
                        services.Add(udpService);
                    }
                    else if (service.ServiceType == KnpServiceType.Tcp)
                    {
                        var parameters = new KnpTcpKcpParameters(service.WindowSize, service.QueueSize, service.UpdateInterval, service.NoDelay);
                        var tcpService = new KnpTcpService(service.Name, listenEndPoint, parameters, _logger);
                        virtualBus.AddService(tcpService);
                        services.Add(tcpService);
                    }
                    else
                    {
                        throw new ArgumentException($"Invalid service type for service {service.Name}.", nameof(options));
                    }
                }
            }
            _services = services.Count == 0 ? null : services.ToArray();

            // prepare all providers
            var factories = new List<KnpProviderFactory>();
            if (options.Providers is not null)
            {
                foreach (KnpProviderDescription provider in options.Providers)
                {
                    if (provider.ServiceType != KnpServiceType.Tcp && provider.ServiceType != KnpServiceType.Udp)
                    {
                        throw new ArgumentException($"Invalid service type for provider {provider.Name}.", nameof(options));
                    }
                    provider.ThrowValidationError();
                    if (string.IsNullOrEmpty(provider.VirtualBus))
                    {
                        factories.Add(new KnpDirectProviderFactory(provider, _logger));
                    }
                    else
                    {
                        KnpVirtualBusLifetime virtualBus = virtualBusCollection.GetOrCreate(provider.VirtualBus);
                        virtualBus.AddProvider(new KnpDirectProviderFactory(provider, _logger));
                    }
                }
            }

            if (!virtualBusCollection.IsEmpty)
            {
                factories.Add(virtualBusCollection);
                _virtualBusCollection = virtualBusCollection;
                _peerConnectionReceiver = peerConnectionReceiver;
                Log.LogClientP2PModeAlert(_logger);
            }
            else
            {
                _virtualBusCollection = null;
                _peerConnectionReceiver = null;
            }

            _factories = factories.ToArray();

            if (_services is not null)
            {
                foreach (IKnpService serviceKv in _services)
                {
                    serviceKv.Start();
                }
            }
        }

        public async Task RunAsync(CancellationToken cancellationToken)
        {
            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    Log.LogClientConnecting(_logger);
                    TimeSpan delay = TimeSpan.FromSeconds(10);
                    do
                    {
                        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                        _connectionClosedCts = cts;
                        KnpPeerConnectionReceiverTransportRegistration peerConnectionReceiverRegistration = default;
                        using var networkTransport = new KcpSocketNetworkTransport(_mtu, _bufferPool);
                        KcpNetworkConnection? connection = null;
                        try
                        {
                            _connectionClosed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

                            // connect
                            int sessionId;
                            uint serverId;
                            networkTransport.SetExceptionHandler((ex, _, state) => Log.LogClientUnhandledException((ILogger?)state!, ex), _logger);
                            EndPoint receiveEndPoint;
                            EndPoint? sendEndPoint;
                            if (_peerConnectionReceiver is null)
                            {
                                await networkTransport.ConnectAsync(_endPoint, cts.Token).ConfigureAwait(false);
                                if (networkTransport.RemoteEndPoint is null)
                                {
                                    Log.LogClientConnectFailed(_logger, _endPoint);
                                    break;
                                }
                                sendEndPoint = receiveEndPoint = networkTransport.RemoteEndPoint;
                            }
                            else
                            {
                                sendEndPoint = await ResolveAsync(_endPoint, cts.Token).ConfigureAwait(false);
                                if (sendEndPoint is null)
                                {
                                    Log.LogClientConnectFailed(_logger, _endPoint);
                                    break;
                                }
                                if (sendEndPoint.AddressFamily == AddressFamily.InterNetwork)
                                {
                                    sendEndPoint = new IPEndPoint(((IPEndPoint)sendEndPoint).Address.MapToIPv6(), ((IPEndPoint)sendEndPoint).Port);
                                }
                                receiveEndPoint = new IPEndPoint(IPAddress.IPv6Any, 0);
                                networkTransport.Bind(receiveEndPoint);
                            }
                            networkTransport.Start(receiveEndPoint, 1024);
                            connection = new KcpNetworkConnection(networkTransport, false, sendEndPoint, new KcpNetworkConnectionOptions { BufferPool = _bufferPool, Mtu = _mtu });
                            connection.SetApplicationRegistration(networkTransport.Register(sendEndPoint, connection));
                            connection.SetExceptionHandler((ex, _, state) => Log.LogClientUnhandledException((ILogger?)state!, ex), _logger);

                            // negotiate
                            {
                                using var authCts = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
                                authCts.CancelAfter(TimeSpan.FromSeconds(20));

                                try
                                {
                                    var authenticator = new KnpClientAuthenticator(_mtu, _password);
                                    if (!await connection.NegotiateAsync(authenticator, cts.Token).ConfigureAwait(false))
                                    {
                                        Log.LogClientAuthenticationFailed(_logger);
                                        break;
                                    }

                                    serverId = authenticator.ServerId;
                                    sessionId = authenticator.SessionId;
                                }
                                catch (OperationCanceledException)
                                {
                                    if (!cts.Token.IsCancellationRequested)
                                    {
                                        Log.LogClientAuthenticationTimeout(_logger);
                                    }
                                    break;
                                }
                            }

                            // setup main connection
                            connection.SetupKeepAlive(TimeSpan.FromSeconds(8), TimeSpan.FromSeconds(45));
                            connection.Register(this);
                            _connection = connection;
                            Log.LogClientConnected(_logger, sessionId);

                            // setup peer connection receiver
                            if (_peerConnectionReceiver is not null)
                            {
                                peerConnectionReceiverRegistration = _peerConnectionReceiver.RegisterTransport(networkTransport, serverId, sessionId);
                            }

                            // create control channel
                            _controlChannel = new KcpConversation(this, new KcpConversationOptions { Mtu = connection.Mss - 4 /* 4: binding id */, BufferPool = _bufferPool });
                            _controlChannel.SetExceptionHandler((ex, _, state) => Log.LogClientUnhandledException((ILogger?)state!, ex), _logger);
                            if (!await RegisterProvidersAsync(_controlChannel, cts.Token).ConfigureAwait(false))
                            {
                                Log.LogClientBindProvidersFailed(_logger);
                                break;
                            }

                            // scan for outdated forward session
                            ThreadPool.UnsafeQueueUserWorkItem(this, false);

                            // wait for connection closed
                            Task completedTask = await Task.WhenAny(_connectionClosed.Task, Task.Delay(Timeout.Infinite, cancellationToken)).ConfigureAwait(false);
                            if (ReferenceEquals(completedTask, _connectionClosed.Task))
                            {
                                Log.LogClientConnectionLost(_logger);
                            }
                        }
                        catch (OperationCanceledException)
                        {
                            if (!cancellationToken.IsCancellationRequested)
                            {
                                Log.LogClientConnectionLost(_logger);
                            }
                            break;
                        }
                        catch (Exception ex)
                        {
                            Log.LogClientUnhandledException(_logger, ex);
                        }
                        finally
                        {
                            if (cancellationToken.IsCancellationRequested)
                            {
                                // host is shutting down
                                // send good-bye messages
                                if (_virtualBusCollection is not null)
                                {
                                    await _virtualBusCollection.DisposeAsync().ConfigureAwait(false);
                                }
                            }

                            if (connection is not null)
                            {
                                await connection.DisposeAsync().ConfigureAwait(false);
                            }

                            cts.Cancel();
                            _connectionClosed = null;
                            _connectionClosedCts = null;
                            Interlocked.Exchange(ref _controlChannel, null)?.Dispose();
                            Interlocked.Exchange(ref _connection, null)?.Dispose();
                            peerConnectionReceiverRegistration.Dispose();
                        }

                    } while (false);

                    // remove current providers
                    KnpCollectionHelper.ClearAndDispose(_providers);
                    KnpCollectionHelper.ClearAndDispose(_forwardSessions);
                    if (cancellationToken.IsCancellationRequested)
                    {
                        break;
                    }

                    // wait and reconnect
                    Log.LogClientConnectRetry(_logger, (int)delay.TotalSeconds);
                    await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
                }
            }
            finally
            {
                if (_services is not null)
                {
                    foreach (IKnpService service in _services)
                    {
                        service.Dispose();
                    }
                }
            }
        }

        private async Task<bool> RegisterProvidersAsync(KcpConversation channel, CancellationToken cancellationToken)
        {
            foreach (KnpProviderFactory factory in _factories)
            {
                await factory.RegisterAsync(channel, this, _providers, cancellationToken).ConfigureAwait(false);
            }

            return true;
        }

        async void IThreadPoolWorkItem.Execute()
        {
            CancellationTokenSource? cts = _connectionClosedCts;
            if (cts is null)
            {
                return;
            }
            CancellationToken cancellationToken = cts.Token;

            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30));
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    Log.LogClientUnhandledException(_logger, ex);
                }

                DateTime utcNow = DateTime.UtcNow;
                foreach (KeyValuePair<long, IKnpForwardSession> item in _forwardSessions)
                {
                    if (item.Value.IsExpired(utcNow))
                    {
                        if (_forwardSessions.TryRemove(item))
                        {
                            item.Value.Dispose();
                        }
                    }
                }
            }

        }

        bool IKnpConnectionHost.TryRegister(long bindingForwardId, IKnpForwardSession forwardSession)
        {
            if (!_forwardSessions.TryAdd(bindingForwardId, forwardSession))
            {
                return false;
            }
            if (_connectionClosedCts is null)
            {
                _forwardSessions.TryRemove(new KeyValuePair<long, IKnpForwardSession>(bindingForwardId, forwardSession));
                return false;
            }

            return true;
        }

        bool IKnpConnectionHost.TryUnregister(long bindingForwardId, IKnpForwardSession forwardSession)
        {
            return _forwardSessions.TryRemove(new KeyValuePair<long, IKnpForwardSession>(bindingForwardId, forwardSession));
        }

        void IKcpNetworkConnectionCallback.NotifyStateChanged(IKcpNetworkConnection connection)
        {
            if (connection.State == KcpNetworkConnectionState.Dead || connection.State == KcpNetworkConnectionState.Failed)
            {
                _connectionClosed?.TrySetResult();
                Interlocked.Exchange(ref _connectionClosedCts, null)?.Cancel();
            }
        }
        ValueTask IKcpNetworkConnectionCallback.PacketReceivedAsync(ReadOnlyMemory<byte> packet, CancellationToken cancellationToken)
        {
            ReadOnlySpan<byte> packetSpan = packet.Span;
            if (packetSpan.Length <= 8)
            {
                return default;
            }
            int bindingId = BinaryPrimitives.ReadInt32BigEndian(packetSpan);

            if (bindingId == 0)
            {
                KcpConversation? controlChannel = _controlChannel;
                if (controlChannel is not null)
                {
                    return controlChannel.InputPakcetAsync(packet.Slice(4), cancellationToken);
                }
                return default;
            }

            if (bindingId > 0)
            {
                long bindingForwardId = MemoryMarshal.Read<long>(packetSpan);

                if (_forwardSessions.TryGetValue(bindingForwardId, out IKnpForwardSession? forwardSession))
                {
                    return forwardSession.InputPacketAsync(packet.Slice(8), cancellationToken);
                }

                if (_providers.TryGetValue(bindingId, out IKnpProvider? provider))
                {
                    return provider.InputPacketAsync(packet, cancellationToken);
                }
                return default;
            }

            return default;
        }

        ValueTask IKcpTransport.SendPacketAsync(Memory<byte> packet, CancellationToken cancellationToken)
        {
            KcpNetworkConnection? connection = _connection;
            if (packet.IsEmpty || connection is null)
            {
                return default;
            }
            using var bufferList = KcpRentedBufferList.Allocate(packet);
            return connection.SendAsync(bufferList.AddPreBuffer(ConstantByteArrayCache.ZeroBytes4), cancellationToken);
        }

        ValueTask IKnpConnectionHost.SendAsync(KcpBufferList buffer, CancellationToken cancellationToken)
        {
            KcpNetworkConnection? connection = _connection;
            if (connection is null)
            {
                return default;
            }
            return connection.SendAsync(buffer, cancellationToken);
        }

        bool IKnpConnectionHost.QueuePacket(KcpBufferList buffer)
        {
            KcpNetworkConnection? connection = _connection;
            if (connection is null)
            {
                return false;
            }
            return connection.Send(buffer);
        }

        private async ValueTask<IPEndPoint?> ResolveAsync(EndPoint endPoint, CancellationToken cancellationToken)
        {
            if (endPoint is IPEndPoint ipep)
            {
                return ipep;
            }
            if (endPoint is not DnsEndPoint dnsEndPoint)
            {
                return null;
            }

            IPAddress[] ips;
            try
            {
                ips = await Dns.GetHostAddressesAsync(dnsEndPoint.Host, cancellationToken).ConfigureAwait(false);
                if (ips.Length == 0)
                {
                    return null;
                }
            }
            catch (Exception ex)
            {
                Log.LogClientHostNameResolvingFailed(_logger, dnsEndPoint.Host, ex);
                return null;
            }

            ipep = new IPEndPoint(ips[0], dnsEndPoint.Port);
            Log.LogClientHostNameResolved(_logger, dnsEndPoint.Host, ipep.Address);
            return ipep;
        }
    }
}
