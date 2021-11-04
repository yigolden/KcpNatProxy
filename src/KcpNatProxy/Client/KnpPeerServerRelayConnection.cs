using System;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using KcpNatProxy.NetworkConnection;
using KcpNatProxy.SocketTransport;
using KcpSharp;

namespace KcpNatProxy.Client
{
    internal sealed class KnpPeerServerRelayConnection : OnceAsyncOperation<bool>, IKnpForwardSession, IKcpNetworkEndPointTransport, IKcpNetworkConnectionCallback, IKcpConnectionKeepAliveContext, IAsyncDisposable
    {
        private readonly KnpPeerConnection _peerConnection;
        private readonly IKnpConnectionHost _host;
        private readonly int _sessionId;
        private readonly byte[] _bindingSessionId;

        private readonly object _stateChangeLock = new();
        private KcpNetworkConnection? _connection;
        private ConnectionStatus _status; // 0-not connected 1-connecting 2-connected 3-degraded 4-disposed
        private long _lastActiveTimeTick;

        enum ConnectionStatus
        {
            NotConnected,
            Negotiating,
            Connected,
            Dead,
        }

        bool IKnpForwardSession.IsExpired(long tick) => _status == ConnectionStatus.Dead;
        EndPoint? IKcpNetworkEndPointTransport.RemoteEndPoint => null;

        public bool IsConnected => _status == ConnectionStatus.Connected;
        public bool IsConnectionDegraded => _status == ConnectionStatus.Connected && (long)((ulong)Environment.TickCount64 - (ulong)Interlocked.Read(ref _lastActiveTimeTick)) > 44 * 1000;

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

        public KnpPeerServerRelayConnection(KnpPeerConnection peerConnection, IKnpConnectionHost host, int bindingId, int sessionId)
        {
            _peerConnection = peerConnection;
            _host = host;
            _sessionId = sessionId;
            _lastActiveTimeTick = Environment.TickCount64;

            _bindingSessionId = new byte[8];
            BinaryPrimitives.WriteInt32BigEndian(_bindingSessionId, bindingId);
            BinaryPrimitives.WriteInt32BigEndian(_bindingSessionId.AsSpan(4), _sessionId);
        }

        public void Start()
        {
            lock (_stateChangeLock)
            {
                if (_status != ConnectionStatus.NotConnected)
                {
                    return;
                }
                _status = ConnectionStatus.Negotiating;
            }
            _host.TryRegister(MemoryMarshal.Read<long>(_bindingSessionId), this);
            base.TryReset();
            base.TryInitiate();
        }

        public void Dispose()
        {
            if (_status == ConnectionStatus.Dead)
            {
                return;
            }
            KcpNetworkConnection? connection;
            lock (_stateChangeLock)
            {
                if (_status == ConnectionStatus.Dead)
                {
                    return;
                }
                _status = ConnectionStatus.Dead;

                connection = _connection;
                _connection = null;
            }

            _connection?.Dispose();
            base.Close();
            _host.TryUnregister(MemoryMarshal.Read<long>(_bindingSessionId), this);
        }

        public async ValueTask DisposeAsync()
        {
            if (_status == ConnectionStatus.Dead)
            {
                return;
            }
            KcpNetworkConnection? connection;
            lock (_stateChangeLock)
            {
                if (_status == ConnectionStatus.Dead)
                {
                    return;
                }
                _status = ConnectionStatus.Dead;

                connection = _connection;
                _connection = null;
            }

            if (connection is not null)
            {
                await connection.DisposeAsync().ConfigureAwait(false);
                try
                {
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                    using var bufferList = KcpRentedBufferList.Allocate(ConstantByteArrayCache.FFByte);
                    await _host.SendAsync(bufferList.AddPreBuffer(_bindingSessionId), cts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // Ignore
                }
            }

            base.Close();
            _host.TryUnregister(MemoryMarshal.Read<long>(_bindingSessionId), this);
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

        protected override async Task<bool> ExecuteAsync(CancellationToken cancellationToken)
        {
            lock (_stateChangeLock)
            {
                if (_status != ConnectionStatus.Negotiating)
                {
                    return _status == ConnectionStatus.Connected;
                }
                Debug.Assert(_connection is null);
            }

            Log.LogPeerServerRelayConnectionNegotiating(_peerConnection.Logger, _sessionId);

            bool negotiated = false;
            try
            {
                _connection = new KcpNetworkConnection(this, new KcpNetworkConnectionOptions { Mtu = _host.Mtu - 8, BufferPool = _host.BufferPool });
                var negotiationContext = new KnpPeerServerRelayNegotiationContext(_connection.Mtu);
                negotiated = await _connection.NegotiateAsync(negotiationContext, cancellationToken).ConfigureAwait(false);
                if (negotiated)
                {
                    _connection.SetupKeepAlive(this, TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(90));
                    _connection.Register(this);
                    Log.LogPeerServerRelayConnectionConnected(_peerConnection.Logger, _sessionId, negotiationContext.NegotiatedMtu.GetValueOrDefault());
                }
                else
                {
                    if (_connection.State != KcpNetworkConnectionState.Dead)
                    {
                        Log.LogPeerServerRelayConnectionNegotiationFailed(_peerConnection.Logger, _sessionId);
                    }
                }
                return negotiated;
            }
            finally
            {
                if (!negotiated)
                {
                    _connection?.Dispose();
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

        void IKcpNetworkConnectionCallback.NotifyStateChanged(IKcpNetworkConnection connection)
        {
            if (connection.State == KcpNetworkConnectionState.Dead)
            {
                Log.LogPeerServerRelayConnectionClosed(_peerConnection.Logger, _sessionId);
            }
            if (connection.State == KcpNetworkConnectionState.Failed || connection.State == KcpNetworkConnectionState.Dead)
            {
                lock (_stateChangeLock)
                {
                    if (_status != ConnectionStatus.NotConnected && _status != ConnectionStatus.Dead)
                    {
                        _status = ConnectionStatus.NotConnected;
                        _connection = null;
                        _host.TryUnregister(MemoryMarshal.Read<long>(_bindingSessionId), this);
                    }
                }
            }
        }

        public ValueTask InputPacketAsync(ReadOnlyMemory<byte> packet, CancellationToken cancellationToken)
        {
            KcpNetworkConnection? connection = _connection;
            if (connection is null)
            {
                return default;
            }
            return connection.InputPacketAsync(packet, cancellationToken);
        }
        ValueTask IKcpNetworkEndPointTransport.QueueAndSendPacketAsync(KcpBufferList packet, CancellationToken cancellationToken)
        {
            if (_status == ConnectionStatus.Dead)
            {
                return default;
            }
            return _host.SendAsync(packet.AddPreBuffer(_bindingSessionId), cancellationToken);
        }
        ValueTask IKcpNetworkEndPointTransport.QueueAndSendPacketAsync(ReadOnlyMemory<byte> packet, CancellationToken cancellationToken)
        {
            if (_status == ConnectionStatus.Dead)
            {
                return default;
            }

            using var bufferList = KcpRentedBufferList.Allocate(packet);
            return _host.SendAsync(bufferList.AddPreBuffer(_bindingSessionId), cancellationToken);
        }
        bool IKcpNetworkEndPointTransport.QueuePacket(KcpBufferList packet)
        {
            return _host.QueuePacket(packet.AddPreBuffer(_bindingSessionId));
        }
        void IKcpExceptionProducer<IKcpNetworkEndPointTransport>.SetExceptionHandler(Func<Exception, IKcpNetworkEndPointTransport, object?, bool> handler, object? state)
        {
            // Do nothing
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

    }
}
