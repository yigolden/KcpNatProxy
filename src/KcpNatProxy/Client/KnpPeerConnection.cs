using System;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using KcpSharp;
using Microsoft.Extensions.Logging;

namespace KcpNatProxy.Client
{
    internal sealed class KnpPeerConnection : IKnpConnectionHost, IThreadPoolWorkItem, IAsyncDisposable, IDisposable
    {
        private readonly KnpPeerConnectionCollection _parent;
        private readonly int _sessionId;
        private ConcurrentDictionary<int, IKnpVirtualBusProviderFactory>? _factoryMappings;

        private readonly ConcurrentDictionary<int, IKnpServiceBinding> _serviceBindings = new();
        private readonly ConcurrentDictionary<int, IKnpProvider> _providers = new();
        private readonly ConcurrentDictionary<long, IKnpForwardSession> _forwardSessions = new();

        private readonly object _stateChangeLock = new();
        private long _initializeTimeTicks;
        private bool _detached;
        private bool _disposed;
        private CancellationTokenSource? _cts;

        private KnpPeerServerReflexConnection? _serverReflexConnection;
        private KnpPeerServerRelayConnection? _serverRelayConnection;

        public bool IsConnected => (_serverReflexConnection?.IsConnected).GetValueOrDefault() || (_serverRelayConnection?.IsConnected).GetValueOrDefault();

        public IKcpBufferPool BufferPool => _parent.BufferPool;
        public ILogger Logger => _parent.Logger;
        EndPoint? IKnpConnectionHost.RemoteEndPoint => null;
        int IKnpConnectionHost.Mtu
        {
            get
            {
                KnpPeerServerRelayConnection? serverRelayConnection = _serverRelayConnection;
                if (serverRelayConnection is not null && serverRelayConnection.TryGetMss(out int mss))
                {
                    return mss;
                }
                KnpPeerServerReflexConnection? serverReflexConnection = _serverReflexConnection;
                if (serverReflexConnection is not null && serverReflexConnection.TryGetMss(out mss))
                {
                    return mss;
                }
                return _parent.MaximumMtu - 16;
            }
        }

        public bool IsExpired(long currentTicks)
        {
            if (_disposed)
            {
                return true;
            }

            if (_detached)
            {
                return _forwardSessions.IsEmpty;
            }

            long initializeTime = Interlocked.Read(ref _initializeTimeTicks);
            if (initializeTime == 0)
            {
                // not initialized
                return false;
            }

            bool connected = false;
            connected |= (_serverReflexConnection?.IsConnected).GetValueOrDefault();
            connected |= (_serverRelayConnection?.IsConnected).GetValueOrDefault();
            if (!connected)
            {
                // not connected in 30 seconds
                return (long)((ulong)currentTicks - (ulong)initializeTime) > 30 * 1000;
            }

            return false;
        }

        bool IKnpConnectionHost.TryGetSessionId(out int sessionId, [NotNullWhen(true)] out byte[]? sessionIdBytes)
        {
            sessionId = default;
            sessionIdBytes = default;
            return false;
        }

        public KnpPeerConnection(KnpPeerConnectionCollection parent, int sessionId)
        {
            _parent = parent;
            _sessionId = sessionId;
        }

        public void Detach()
        {
            _detached = true;
        }

        public void Initialize(KnpVirtualBusControlChannel controlChannel, ConcurrentDictionary<int, IKnpVirtualBusProviderFactory> factoryMappings, bool connectNow)
        {
            if (_disposed || _detached || Interlocked.Read(ref _initializeTimeTicks) != 0)
            {
                return;
            }
            KnpPeerServerReflexConnection serverReflexConnection;
            lock (_stateChangeLock)
            {
                if (_disposed || _detached || Interlocked.Read(ref _initializeTimeTicks) != 0)
                {
                    return;
                }
                long ticks = Environment.TickCount64;
                if (ticks == 0)
                {
                    ticks = 1;
                }
                Interlocked.Exchange(ref _initializeTimeTicks, ticks);
                _factoryMappings = factoryMappings;

                _cts = new CancellationTokenSource();

                _serverReflexConnection = serverReflexConnection = new KnpPeerServerReflexConnection(this, _parent.PeerConnectionReceiver, _sessionId);
            }

            if (connectNow)
            {
                serverReflexConnection.Connect(controlChannel);
            }
            Log.LogPeerConnectionCreating(_parent.Logger, _sessionId);
            ThreadPool.UnsafeQueueUserWorkItem(this, false);
        }

        public void ConnectPeer(long accessSecret, ReadOnlySpan<byte> endPoint)
        {
            if (endPoint.Length != 18)
            {
                return;
            }

            KnpPeerServerReflexConnection? serverReflexConnection;
            lock (_stateChangeLock)
            {
                serverReflexConnection = _serverReflexConnection;
                if (_disposed || _detached || Interlocked.Read(ref _initializeTimeTicks) == 0 || serverReflexConnection is null)
                {
                    return;
                }
            }

            serverReflexConnection.Connect(accessSecret, new IPEndPoint(new IPAddress(endPoint.Slice(0, 16)), BinaryPrimitives.ReadUInt16BigEndian(endPoint.Slice(16, 2))));
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }
            KnpPeerServerRelayConnection? serverRelayConnection;
            KnpPeerServerReflexConnection? serverReflexConnection;
            CancellationTokenSource? cts;
            lock (_stateChangeLock)
            {
                if (_disposed)
                {
                    return;
                }
                _disposed = true;

                serverRelayConnection = _serverRelayConnection;
                _serverRelayConnection = null;
                serverReflexConnection = _serverReflexConnection;
                _serverReflexConnection = null;
                cts = _cts;
                _cts = null;
            }

            if (cts is not null)
            {
                cts.Cancel();
                cts.Dispose();
            }

            _parent.NotifyPeerConnectionDisposed(_sessionId, this);
            KnpCollectionHelper.ClearAndDispose(_forwardSessions);
            KnpCollectionHelper.ClearAndDispose(_serviceBindings);
            KnpCollectionHelper.ClearAndDispose(_providers);

            serverRelayConnection?.Dispose();
            serverReflexConnection?.Dispose();
        }

        public async ValueTask DisposeAsync()
        {
            if (_disposed)
            {
                return;
            }
            KnpPeerServerRelayConnection? serverRelayConnection;
            KnpPeerServerReflexConnection? serverReflexConnection;
            CancellationTokenSource? cts;
            lock (_stateChangeLock)
            {
                if (_disposed)
                {
                    return;
                }
                _disposed = true;

                serverRelayConnection = _serverRelayConnection;
                _serverRelayConnection = null;
                serverReflexConnection = _serverReflexConnection;
                _serverReflexConnection = null;
                cts = _cts;
                _cts = null;
            }

            if (cts is not null)
            {
                cts.Cancel();
                cts.Dispose();
            }

            _parent.NotifyPeerConnectionDisposed(_sessionId, this);
            KnpCollectionHelper.ClearAndDispose(_forwardSessions);
            KnpCollectionHelper.ClearAndDispose(_serviceBindings);
            KnpCollectionHelper.ClearAndDispose(_providers);

            if (serverRelayConnection is not null)
            {
                await serverRelayConnection.DisposeAsync().ConfigureAwait(false);
            }
            if (serverReflexConnection is not null)
            {
                await serverReflexConnection.DisposeAsync().ConfigureAwait(false);
            }
        }

        public void SetupServerRelay(IKnpConnectionHost host, int bindingId)
        {
            if (_disposed || _detached || Interlocked.Read(ref _initializeTimeTicks) == 0 || _serverRelayConnection is not null)
            {
                return;
            }
            KnpPeerServerRelayConnection serverRelayConnection;
            lock (_stateChangeLock)
            {
                if (_disposed || _detached || Interlocked.Read(ref _initializeTimeTicks) == 0 || _serverRelayConnection is not null)
                {
                    return;
                }

                _serverRelayConnection = serverRelayConnection = new KnpPeerServerRelayConnection(this, host, bindingId, _sessionId);
            }

            serverRelayConnection.Start();
        }

        public void NotifyProviderParameters(int bindingId, ReadOnlySpan<byte> parameters)
        {
            if (_providers.ContainsKey(bindingId))
            {
                return;
            }
            if (_factoryMappings is null || !_factoryMappings.TryGetValue(bindingId, out IKnpVirtualBusProviderFactory? factory))
            {
                return;
            }
            if (_disposed)
            {
                return;
            }
            IKnpProvider? provider = factory.CreateProvider(this, bindingId, parameters);
            if (!_providers.TryAdd(bindingId, provider))
            {
                provider.Dispose();
                return;
            }
            if (_disposed)
            {
                if (_providers.TryRemove(new KeyValuePair<int, IKnpProvider>(bindingId, provider)))
                {
                    provider.Dispose();
                }
                return;
            }
        }

        public void CreateBinding(IKnpService service, int bindingId, ReadOnlySpan<byte> parameters)
        {
            if (_disposed || _serviceBindings.ContainsKey(bindingId))
            {
                return;
            }

            IKnpServiceBinding? serviceBinding = service.CreateBinding(this, new KnpRentedInt32(bindingId), parameters);
            if (serviceBinding is null)
            {
                return;
            }
            if (!_serviceBindings.TryAdd(bindingId, serviceBinding))
            {
                serviceBinding.Dispose();
                return;
            }
            if (_disposed)
            {
                if (_serviceBindings.TryRemove(new KeyValuePair<int, IKnpServiceBinding>(bindingId, serviceBinding)))
                {
                    serviceBinding.Dispose();
                }
                return;
            }
        }

        public ValueTask<bool> WaitForConnectionAsync(CancellationToken cancellationToken)
        {
            KnpPeerServerReflexConnection? serverReflexConnection = Volatile.Read(ref _serverReflexConnection);
            KnpPeerServerRelayConnection? serverRelayConnection = Volatile.Read(ref _serverRelayConnection);
            if (_disposed)
            {
                return default;
            }
            if (serverReflexConnection is null && serverRelayConnection is null)
            {
                return default;
            }
            if (serverRelayConnection is null)
            {
                Debug.Assert(serverReflexConnection is not null);
                return serverReflexConnection.WaitForConnectionAsync(cancellationToken);
            }
            if (serverReflexConnection is null)
            {
                return serverRelayConnection.WaitForConnectionAsync(cancellationToken);
            }
            if (serverReflexConnection.IsConnected || serverRelayConnection.IsConnected)
            {
                return new ValueTask<bool>(true);
            }
            return new ValueTask<bool>(WaitForConnectionAsync(serverReflexConnection, serverRelayConnection, cancellationToken));
        }

        private static async Task<bool> WaitForConnectionAsync(KnpPeerServerReflexConnection serverReflexConnection, KnpPeerServerRelayConnection serverRelayConnection, CancellationToken cancellationToken)
        {
            Task<bool> serverReflexConnectionTask = serverReflexConnection.WaitForConnectionAsync(cancellationToken).AsTask();
            Task<bool> serverRelayConnectionTask = serverRelayConnection.WaitForConnectionAsync(cancellationToken).AsTask();
            Task<bool> completedTask = await Task.WhenAny(serverReflexConnectionTask, serverRelayConnectionTask).ConfigureAwait(false);
            if (await completedTask.ConfigureAwait(false))
            {
                return true;
            }
            completedTask = ReferenceEquals(completedTask, serverReflexConnectionTask) ? serverRelayConnectionTask : serverReflexConnectionTask;
            return await completedTask.ConfigureAwait(false);
        }

        async void IThreadPoolWorkItem.Execute()
        {
            CancellationToken cancellationToken;
            lock (_stateChangeLock)
            {
                if (_disposed || Interlocked.Read(ref _initializeTimeTicks) == 0 || _cts is null)
                {
                    return;
                }
                cancellationToken = _cts.Token;
            }


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
                    Log.LogClientUnhandledException(_parent.Logger, ex);
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

        internal ValueTask InputRelayPacketAsync(ReadOnlyMemory<byte> packet, CancellationToken cancellationToken)
        {
            // packet: KcpNetworkConnection encapsulated
            KnpPeerServerRelayConnection? relayConnection = _serverRelayConnection;
            if (relayConnection is not null)
            {
                return relayConnection.InputPacketAsync(packet, cancellationToken);
            }
            return default;
        }

        internal ValueTask ProcessRemotePacketAsync(ReadOnlyMemory<byte> packet, CancellationToken cancellationToken)
        {
            if (_disposed || packet.Length <= 8)
            {
                return default;
            }

            if (_forwardSessions.TryGetValue(MemoryMarshal.Read<long>(packet.Span), out IKnpForwardSession? forwardSession))
            {
                return forwardSession.InputPacketAsync(packet.Slice(8), cancellationToken);
            }

            int bindingId = BinaryPrimitives.ReadInt32BigEndian(packet.Span);
            if (_providers.TryGetValue(bindingId, out IKnpProvider? provider))
            {
                return provider.InputPacketAsync(packet, cancellationToken);
            }

            if (_serviceBindings.TryGetValue(bindingId, out IKnpServiceBinding? serviceBinding))
            {
                return serviceBinding.InputPacketAsync(packet, cancellationToken);
            }

            ConcurrentDictionary<int, IKnpVirtualBusProviderFactory>? factoryMappings = _factoryMappings;
            if (factoryMappings is null || !factoryMappings.TryGetValue(bindingId, out IKnpVirtualBusProviderFactory? factory))
            {
                return default;
            }
            provider = factory.CreateProvider(this, bindingId, default);

            if (_disposed || !_providers.TryAdd(bindingId, provider))
            {
                provider.Dispose();
                return default;
            }

            if (_disposed)
            {
                if (_providers.TryRemove(new KeyValuePair<int, IKnpProvider>(bindingId, provider)))
                {
                    provider.Dispose();
                    return default;
                }
            }

            return provider.InputPacketAsync(packet, cancellationToken);
        }

        bool IKnpConnectionHost.TryRegister(long bindingForwardId, IKnpForwardSession forwardSession)
        {
            if (_disposed)
            {
                return false;
            }
            if (!_forwardSessions.TryAdd(bindingForwardId, forwardSession))
            {
                return false;
            }
            if (_disposed)
            {
                _forwardSessions.TryRemove(new KeyValuePair<long, IKnpForwardSession>(bindingForwardId, forwardSession));
                return false;
            }
            return true;
        }
        bool IKnpConnectionHost.TryUnregister(long bindingForwardId, IKnpForwardSession forwardSession)
        {
            if (_disposed)
            {
                return false;
            }
            if (!_forwardSessions.TryRemove(new KeyValuePair<long, IKnpForwardSession>(bindingForwardId, forwardSession)))
            {
                return false;
            }
            return true;
        }

        ValueTask IKnpConnectionHost.SendAsync(KcpBufferList bufferList, CancellationToken cancellationToken)
        {
            if (_disposed)
            {
                return default;
            }
            int length = bufferList.GetLength();
            KnpPeerServerReflexConnection? serverReflexConnection = _serverReflexConnection;
            if (serverReflexConnection is not null && (!serverReflexConnection.IsConnected || !serverReflexConnection.CheckMss(length)))
            {
                serverReflexConnection = null;
            }
            if (serverReflexConnection is not null && !serverReflexConnection.IsConnectionDegraded)
            {
                return serverReflexConnection.SendAsync(bufferList, cancellationToken);
            }
            KnpPeerServerRelayConnection? relayConnection = _serverRelayConnection;
            if (relayConnection is not null && relayConnection.IsConnected && relayConnection.CheckMss(length))
            {
                return relayConnection.SendAsync(bufferList, cancellationToken);
            }
            return default;
        }

        bool IKnpConnectionHost.QueuePacket(KcpBufferList bufferList)
        {
            if (_disposed)
            {
                return default;
            }
            int length = bufferList.GetLength();
            KnpPeerServerReflexConnection? serverReflexConnection = _serverReflexConnection;
            if (serverReflexConnection is not null && (!serverReflexConnection.IsConnected || !serverReflexConnection.CheckMss(length)))
            {
                serverReflexConnection = null;
            }
            if (serverReflexConnection is not null && !serverReflexConnection.IsConnectionDegraded)
            {
                return serverReflexConnection.Send(bufferList);
            }
            KnpPeerServerRelayConnection? relayConnection = _serverRelayConnection;
            if (relayConnection is not null && relayConnection.IsConnected && relayConnection.CheckMss(length))
            {
                return relayConnection.Send(bufferList);
            }
            return default;
        }

    }
}
