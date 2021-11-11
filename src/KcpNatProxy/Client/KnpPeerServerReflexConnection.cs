using System;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using KcpNatProxy.NetworkConnection;

namespace KcpNatProxy.Client
{
    internal sealed class KnpPeerServerReflexConnection : OnceAsyncOperation<bool>, IKcpNetworkConnectionCallback, IKnpPeerConnectionReceiverPeerCallback, IKcpConnectionKeepAliveContext, IThreadPoolWorkItem
    {
        private readonly KnpPeerConnection _peerConnection;
        private readonly KnpPeerConnectionReceiver _receiver;
        private readonly int _sessionId;

        private readonly object _stateChangeLock = new();
        private ConnectionStatus _status;
        private CancellationTokenSource? _cts;
        // querying
        private KnpVirtualBusControlChannel? _controlChannel;
        // probing
        private KnpVirtualBusPeerConnectionInfo _connectionInfo;
        // connection
        private KcpNetworkConnection? _connection;
        private long _lastActiveTimeTick;

        enum ConnectionStatus
        {
            NotConnected,
            Querying,
            Probing,
            Negotiating,
            Connected,
            Dead
        }

        public bool IsConnected => _status == ConnectionStatus.Connected;
        public bool IsConnectionDegraded => _status == ConnectionStatus.Connected && (long)((ulong)Environment.TickCount64 - (ulong)Interlocked.Read(ref _lastActiveTimeTick)) > 24 * 1000;

        public bool TryGetMss(out int mss)
        {
            KcpNetworkConnection? connection = _connection;
            if (connection is null)
            {
                mss = 0;
                return false;
            }
            mss = connection.Mss;
            return true;
        }

        public bool CheckMss(int mss)
        {
            KcpNetworkConnection? connection = _connection;
            return connection is not null && mss <= connection.Mss;
        }

        public KnpPeerServerReflexConnection(KnpPeerConnection peerConnection, KnpPeerConnectionReceiver receiver, int sessionId)
            : base(manualExecution: true)
        {
            _peerConnection = peerConnection;
            _receiver = receiver;
            _sessionId = sessionId;
            _lastActiveTimeTick = Environment.TickCount64;
        }

        public void Dispose()
        {
            if (_status == ConnectionStatus.Dead)
            {
                return;
            }

            CancellationTokenSource? cts;
            KcpNetworkConnection? connection;
            KnpVirtualBusPeerConnectionInfo connectionInfo;
            lock (_stateChangeLock)
            {
                if (_status == ConnectionStatus.Dead)
                {
                    return;
                }
                _status = ConnectionStatus.Dead;

                _controlChannel = null;
                cts = _cts;
                _cts = null;
                connectionInfo = _connectionInfo;
                _connectionInfo = default;
                connection = _connection;
                _connection = null;
            }

            base.Close();

            if (cts is not null)
            {
                cts.Cancel();
                cts.Dispose();
            }

            if (!connectionInfo.IsEmpty)
            {
                _receiver.TryUnregister(connectionInfo.AccessSecret, this);
            }

            if (connection is not null)
            {
                connection.Dispose();
            }
        }

        public ValueTask DisposeAsync()
        {
            if (_status == ConnectionStatus.Dead)
            {
                return default;
            }

            CancellationTokenSource? cts;
            KcpNetworkConnection? connection;
            KnpVirtualBusPeerConnectionInfo connectionInfo;
            lock (_stateChangeLock)
            {
                if (_status == ConnectionStatus.Dead)
                {
                    return default;
                }
                _status = ConnectionStatus.Dead;

                _controlChannel = null;
                cts = _cts;
                _cts = null;
                connectionInfo = _connectionInfo;
                _connectionInfo = default;
                connection = _connection;
                _connection = null;
            }

            base.Close();

            if (cts is not null)
            {
                cts.Cancel();
                cts.Dispose();
            }

            if (!connectionInfo.IsEmpty)
            {
                _receiver.TryUnregister(connectionInfo.AccessSecret, this);
            }

            if (connection is not null)
            {
                return connection.DisposeAsync();
            }

            return default;
        }

        public void Connect(KnpVirtualBusControlChannel controlChannel)
        {
            lock (_stateChangeLock)
            {
                if (_status != ConnectionStatus.NotConnected)
                {
                    return;
                }
                _status = ConnectionStatus.Querying;

                _controlChannel = controlChannel;
                _cts = new CancellationTokenSource();
            }

            Log.LogPeerServerReflexConnectionCreatingInQueryMode(_peerConnection.Logger, _sessionId);
            base.TryReset();
            ThreadPool.UnsafeQueueUserWorkItem(this, false);
        }

        public void Connect(long accessSecret, IPEndPoint remoteEndPoint)
        {
            lock (_stateChangeLock)
            {
                if (_status != ConnectionStatus.NotConnected)
                {
                    return;
                }
                _status = ConnectionStatus.Probing;

                _cts = new CancellationTokenSource();
                _connectionInfo = new KnpVirtualBusPeerConnectionInfo(remoteEndPoint, accessSecret);
            }

            Log.LogPeerServerReflexConnectionCreatingInConnectMode(_peerConnection.Logger, _sessionId, remoteEndPoint);
            base.TryReset();
            ThreadPool.UnsafeQueueUserWorkItem(this, false);
        }

        async void IThreadPoolWorkItem.Execute()
        {
            CancellationTokenSource? cts = null;
            CancellationToken cancellationToken;
            KnpVirtualBusControlChannel? controlChannel;
            KnpVirtualBusPeerConnectionInfo connectionInfo = default;

            lock (_stateChangeLock)
            {
                controlChannel = _controlChannel;
                if (_status == ConnectionStatus.Querying)
                {
                    if (_cts is null || controlChannel is null)
                    {
                        return;
                    }
                    cancellationToken = _cts.Token;
                }
                else if (_status == ConnectionStatus.Probing)
                {
                    if (_cts is null || _connectionInfo.IsEmpty)
                    {
                        return;
                    }
                    cancellationToken = _cts.Token;
                    connectionInfo = _connectionInfo;
                }
                else
                {
                    return;
                }
            }

            // querying phase
            if (controlChannel is not null && connectionInfo.IsEmpty)
            {
                Log.LogPeerServerReflexConnectionQuerying(_peerConnection.Logger, _sessionId);
                try
                {
                    connectionInfo = await controlChannel.ConnectPeerAsync(_sessionId, cancellationToken).ConfigureAwait(false);
                    if (!connectionInfo.IsEmpty)
                    {
                        Log.LogPeerServerReflexConnectionQueryComplete(_peerConnection.Logger, _sessionId, connectionInfo.EndPoint);
                    }
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception ex)
                {
                    Log.LogClientUnhandledException(_peerConnection.Logger, ex);
                }
                finally
                {
                    lock (_stateChangeLock)
                    {
                        if (connectionInfo.IsEmpty)
                        {
                            if (_status == ConnectionStatus.Querying)
                            {
                                _status = ConnectionStatus.NotConnected;
                                _controlChannel = null;
                                cts = _cts;
                                _cts = null;
                            }
                        }
                        else
                        {
                            if (_status == ConnectionStatus.Querying)
                            {
                                _status = ConnectionStatus.Probing;
                                _connectionInfo = connectionInfo;
                            }
                            else
                            {
                                connectionInfo = default;
                            }
                        }

                        if (cts is not null)
                        {
                            cts.Cancel();
                            cts.Dispose();
                        }
                    }
                }
            }

            if (connectionInfo.IsEmpty)
            {
                return;
            }

            // probing phase
            Log.LogPeerServerReflexConnectionProbingStarted(_peerConnection.Logger, _sessionId, connectionInfo.EndPoint);
            _receiver.TryRegister(connectionInfo.AccessSecret, this);

            IPEndPoint probeEndPoint1 = connectionInfo.EndPoint;
            IPEndPoint? probeEndPoint2 = null;
            if (probeEndPoint1.Port >= 1024 && probeEndPoint1.Port < ushort.MaxValue)
            {
                probeEndPoint2 = new IPEndPoint(probeEndPoint1.Address, probeEndPoint1.Port + 1);
            }
            try
            {
                using var timer = new PeriodicTimer(TimeSpan.FromSeconds(0.5));
                int countdown = 20; // stop trying after 10 seconds;
                while (!cancellationToken.IsCancellationRequested && countdown > 0)
                {
                    // send probe message
                    await _receiver.SendProbingMessageAsync(_sessionId, connectionInfo.AccessSecret, probeEndPoint1, cancellationToken).ConfigureAwait(false);
                    if (probeEndPoint2 is not null)
                    {
                        await _receiver.SendProbingMessageAsync(_sessionId, connectionInfo.AccessSecret, probeEndPoint2, cancellationToken).ConfigureAwait(false);
                    }

                    countdown--;

                    // wait
                    await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                // Ignore
            }
            catch (Exception ex)
            {
                Log.LogClientUnhandledException(_peerConnection.Logger, ex);
            }
            finally
            {
                // clean up
                bool cleanup = false;
                lock (_stateChangeLock)
                {
                    if (_status == ConnectionStatus.Probing)
                    {
                        cts = _cts;
                        _cts = null;
                        _connectionInfo = default;
                        cleanup = true;
                    }
                }
                if (cts is not null)
                {
                    cts.Cancel();
                    cts.Dispose();
                }
                if (cleanup)
                {
                    _receiver.TryUnregister(connectionInfo.AccessSecret, this);
                    Log.LogPeerServerReflexConnectionProbingUnresponsive(_peerConnection.Logger, _sessionId, connectionInfo.EndPoint);
                }
            }
        }

        void IKnpPeerConnectionReceiverPeerCallback.OnTransportClosed()
        {
            // stop probing
            CancellationTokenSource? cts = null;
            lock (_stateChangeLock)
            {
                if (_status == ConnectionStatus.Probing)
                {
                    _status = ConnectionStatus.NotConnected;
                    cts = _cts;
                    _cts = null;
                    _connectionInfo = default;
                }
            }

            if (cts is not null)
            {
                cts.Cancel();
                cts.Dispose();
            }
        }

        bool IKnpPeerConnectionReceiverPeerCallback.OnPeerProbing(int sessionId, EndPoint remoteEndPoint, KcpNetworkConnection networkConnection)
        {
            if (_sessionId != sessionId)
            {
                return false;
            }

            Log.LogPeerServerReflexConnectionProbingReceived(_peerConnection.Logger, remoteEndPoint, _sessionId);

            // TODO should we transit from NotConnected to Negotiating state ?

            CancellationTokenSource? cts;
            long accessSecret;
            lock (_stateChangeLock)
            {
                if (_status != ConnectionStatus.Probing)
                {
                    return false;
                }
                _status = ConnectionStatus.Negotiating;

                accessSecret = _connectionInfo.AccessSecret;
                cts = _cts;
                _cts = null;

                _connection = networkConnection;
            }

            if (cts is not null)
            {
                cts.Cancel();
                cts.Dispose();
            }

            _receiver.TryUnregister(accessSecret, this);

            // start negotiating
            base.TryInitiate();
            return true;
        }

        protected override async Task<bool> ExecuteAsync(CancellationToken cancellationToken)
        {
            KcpNetworkConnection? connection;
            long accessSecret;
            lock (_stateChangeLock)
            {
                if (_status != ConnectionStatus.Negotiating || _connection is null || _connectionInfo.IsEmpty)
                {
                    return false;
                }
                connection = _connection;
                accessSecret = _connectionInfo.AccessSecret;
                _connectionInfo = default;
            }

            Log.LogPeerServerReflexConnectionNegotiating(_peerConnection.Logger, _sessionId);

            bool negotiated = false;
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(10));
            try
            {
                var negotiationContext = new KnpPeerServerReflexConnectionNegotiationContext(_receiver.Mtu, _receiver.ServerId, _sessionId, _receiver.SessionId, accessSecret);
                negotiated = await connection.NegotiateAsync(negotiationContext, cts.Token).ConfigureAwait(false);
                if (negotiated)
                {
                    connection.SetupKeepAlive(this, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(40));
                    connection.Register(this);
                    Log.LogPeerServerReflexConnectionConnected(_peerConnection.Logger, _sessionId, connection.RemoteEndPoint, negotiationContext.NegotiatedMtu.GetValueOrDefault());
                }
                else
                {
                    if (connection.State != KcpNetworkConnectionState.Dead)
                    {
                        Log.LogPeerServerReflexConnectionNegotiationFailed(_peerConnection.Logger, _sessionId);
                    }
                }
                return negotiated;
            }
            catch (OperationCanceledException)
            {
                if (cts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
                {
                    Log.LogPeerServerReflexConnectionNegotiationTimeout(_peerConnection.Logger, _sessionId);
                }
                return false;
            }
            finally
            {
                if (!negotiated)
                {
                    connection.Dispose();
                }
                lock (_stateChangeLock)
                {
                    if (_status == ConnectionStatus.Negotiating)
                    {
                        _status = negotiated ? ConnectionStatus.Connected : ConnectionStatus.NotConnected;
                        Interlocked.Exchange(ref _lastActiveTimeTick, Environment.TickCount64);
                    }
                }
            }
        }

        public ValueTask<bool> WaitForConnectionAsync(CancellationToken cancellationToken)
        {
            ConnectionStatus status = _status;
            if (status == ConnectionStatus.NotConnected || status == ConnectionStatus.Dead)
            {
                return default;
            }
            return base.RunAsync(cancellationToken);
        }

        void IKcpNetworkConnectionCallback.NotifyStateChanged(IKcpNetworkConnection connection)
        {
            if (connection.State == KcpNetworkConnectionState.Dead)
            {
                Log.LogPeerServerReflexConnectionClosed(_peerConnection.Logger, _sessionId);
            }
            if (connection.State == KcpNetworkConnectionState.Failed || connection.State == KcpNetworkConnectionState.Dead)
            {
                lock (_stateChangeLock)
                {
                    if (_status != ConnectionStatus.NotConnected && _status != ConnectionStatus.Probing && _status != ConnectionStatus.Dead)
                    {
                        _status = ConnectionStatus.NotConnected;
                        _connection = null;
                    }
                }
            }
        }

        ValueTask IKcpNetworkConnectionCallback.PacketReceivedAsync(ReadOnlyMemory<byte> packet, CancellationToken cancellationToken)
        {
            Interlocked.Exchange(ref _lastActiveTimeTick, Environment.TickCount64);
            return _peerConnection.ProcessRemotePacketAsync(packet, cancellationToken);
        }

        byte IKcpConnectionKeepAliveContext.PreparePayload(Span<byte> buffer) => 0;
        void IKcpConnectionKeepAliveContext.UpdateSample(uint packetsSent, uint packetsAcknowledged, ReadOnlySpan<byte> payload)
        {
            Interlocked.Exchange(ref _lastActiveTimeTick, Environment.TickCount64);
        }


        public ValueTask SendAsync(KcpBufferList bufferList, CancellationToken cancellationToken)
        {
            KcpNetworkConnection? connection = _connection;
            if (connection is null)
            {
                return default;
            }
            return connection.SendAsync(bufferList, cancellationToken);
        }
        public bool Send(KcpBufferList bufferList)
        {
            KcpNetworkConnection? connection = _connection;
            if (connection is null)
            {
                return false;
            }
            return connection.Send(bufferList);
        }

    }
}
