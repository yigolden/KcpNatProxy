using System;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace KcpNatProxy.Client
{
    internal sealed class KnpVirtualBusProvider : IKnpProvider, IThreadPoolWorkItem
    {
        private KnpVirtualBusLifetime? _lifetime;
        private readonly IEnumerable<IKnpService> _services;
        private readonly IEnumerable<IKnpVirtualBusProviderFactory> _providerFactories;
        private readonly IKnpConnectionHost _host;
        private readonly int _bindingId;
        private readonly KnpVirtualBusRelayType _relayType;
        private readonly ILogger _logger;

        private bool _disposed;
        private CancellationTokenSource? _cts;
        private KnpVirtualBusControlChannel? _controlChannel;
        private KnpVirtualBusProviderNotificationReceiver? _notificationReceiver;
        private readonly ConcurrentDictionary<int, IKnpVirtualBusProviderFactory> _factoryMappings = new();

        public KnpVirtualBusProvider(KnpVirtualBusLifetime lifetime, IEnumerable<IKnpService> services, IEnumerable<IKnpVirtualBusProviderFactory> providerFactories, IKnpConnectionHost host, int bindingId, KnpVirtualBusRelayType relayType, ILogger logger)
        {
            _lifetime = lifetime;
            _services = services;
            _providerFactories = providerFactories;
            _host = host;
            _bindingId = bindingId;
            _relayType = relayType;
            _logger = logger;

            _cts = new CancellationTokenSource();
            _controlChannel = new KnpVirtualBusControlChannel(lifetime.Name, host, bindingId);
            _notificationReceiver = new KnpVirtualBusProviderNotificationReceiver(this, host, bindingId);
            ThreadPool.UnsafeQueueUserWorkItem(this, false);

            _notificationReceiver.Start();
        }

        public void Dispose()
        {
            _disposed = true;
            CancellationTokenSource? cts = Interlocked.Exchange(ref _cts, null);
            if (cts is not null)
            {
                cts.Cancel();
                cts.Dispose();
            }
            Interlocked.Exchange(ref _lifetime, null)?.NotifyProviderDisposed();
            Interlocked.Exchange(ref _controlChannel, null)?.Dispose();
            Interlocked.Exchange(ref _notificationReceiver, null)?.Dispose();
        }

        public ValueTask RegisterVirtualBusProvidersAsync(CancellationToken cancellationToken)
        {
            KnpVirtualBusControlChannel? controlChannel = _controlChannel;
            if (controlChannel is null)
            {
                return default;
            }
            return new ValueTask(controlChannel.RegisterProvidersAsync(_providerFactories, _factoryMappings, cancellationToken));
        }

        ValueTask IKnpProvider.InputPacketAsync(ReadOnlyMemory<byte> packet, CancellationToken cancellationToken)
        {
            if (_disposed || packet.Length <= 8)
            {
                return default;
            }

            if (_relayType != KnpVirtualBusRelayType.Always)
            {
                return default;
            }
            KnpVirtualBusLifetime? lifetime = _lifetime;
            KnpVirtualBusControlChannel? controlChannel = _controlChannel;
            if (lifetime is null || controlChannel is null)
            {
                return default;
            }

            // relay to peer connection
            int sessionId = BinaryPrimitives.ReadInt32BigEndian(packet.Span.Slice(4));
            KnpPeerConnection? peerConnection = lifetime.GetOrCreatePeerConnection(sessionId);
            if (peerConnection is null)
            {
                return default;
            }

            peerConnection.Initialize(controlChannel, _factoryMappings, true);
            peerConnection.SetupServerRelay(_host, _bindingId);
            return peerConnection.InputRelayPacketAsync(packet.Slice(8), cancellationToken);
        }

        async void IThreadPoolWorkItem.Execute()
        {
            CancellationTokenSource? cts = _cts;
            if (cts is null)
            {
                return;
            }
            CancellationToken cancellationToken = cts.Token;

            PeriodicTimer? timer = null;
            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    if (timer is null)
                    {
                        timer = new PeriodicTimer(TimeSpan.FromSeconds(40));
                    }
                    else
                    {
                        await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false);
                    }

                    KnpVirtualBusControlChannel? controlChannel = _controlChannel;
                    if (controlChannel is null)
                    {
                        continue;
                    }

                    foreach (IKnpService? service in _services)
                    {
                        if (cancellationToken.IsCancellationRequested)
                        {
                            break;
                        }

                        KnpVirtualBusProviderSessionBinding sessionBinding = await controlChannel.QueryAsync(service, cancellationToken).ConfigureAwait(false);
                        if (sessionBinding.IsEmpty)
                        {
                            continue;
                        }

                        KnpPeerConnection? peerConnection = _lifetime?.GetOrCreatePeerConnection(sessionBinding.SessionId);
                        if (peerConnection is null)
                        {
                            continue;
                        }

                        peerConnection.Initialize(controlChannel, _factoryMappings, _relayType == KnpVirtualBusRelayType.Never || _relayType == KnpVirtualBusRelayType.Mixed);
                        if (_relayType != KnpVirtualBusRelayType.Never)
                        {
                            peerConnection.SetupServerRelay(_host, _bindingId);
                        }

                        bool connected = peerConnection.IsConnected;
                        if (!connected)
                        {
                            using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                            connectCts.CancelAfter(TimeSpan.FromSeconds(10));
                            try
                            {
                                connected = await peerConnection.WaitForConnectionAsync(connectCts.Token).ConfigureAwait(false);
                            }
                            catch (OperationCanceledException)
                            {
                                // Ignore
                            }
                        }

                        if (connected)
                        {
                            peerConnection.CreateBinding(service, sessionBinding.BindingId, sessionBinding.Parameters);
                        }
                    }
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                // TODO log this
                Console.WriteLine(ex.ToString());
            }
            finally
            {
                timer?.Dispose();
            }
        }

        public void NotifyProviderParameters(int sessionId, int bindingId, ReadOnlySpan<byte> parameters)
        {
            KnpVirtualBusLifetime? lifetime = _lifetime;
            KnpVirtualBusControlChannel? controlChannel = _controlChannel;
            if (lifetime is null || controlChannel is null)
            {
                return;
            }

            Log.LogPeerVirtualBusProviderParameterNotificationReceived(_logger, lifetime.Name, sessionId);

            KnpPeerConnection? peerConnection = lifetime.GetOrCreatePeerConnection(sessionId);
            if (peerConnection is null)
            {
                return;
            }

            peerConnection.Initialize(controlChannel, _factoryMappings, false);
            if (_relayType != KnpVirtualBusRelayType.Never)
            {
                peerConnection.SetupServerRelay(_host, _bindingId);
            }

            peerConnection.NotifyProviderParameters(bindingId, parameters);
        }

        public void NotifyPeerToPeerConnection(int sessionId, long accessSecret, ReadOnlySpan<byte> endpoint)
        {
            KnpVirtualBusLifetime? lifetime = _lifetime;
            KnpVirtualBusControlChannel? controlChannel = _controlChannel;
            if (lifetime is null || controlChannel is null)
            {
                return;
            }

            Log.LogPeerVirtualBusPeerConnectNotificationReceived(_logger, lifetime.Name, sessionId);

            KnpPeerConnection? peerConnection = lifetime.GetOrCreatePeerConnection(sessionId);
            if (peerConnection is null)
            {
                return;
            }

            peerConnection.Initialize(controlChannel, _factoryMappings, false);
            peerConnection.ConnectPeer(accessSecret, endpoint);
            if (_relayType != KnpVirtualBusRelayType.Never)
            {
                peerConnection.SetupServerRelay(_host, _bindingId);
            }

        }
    }
}
