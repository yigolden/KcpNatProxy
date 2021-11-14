using System;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using KcpNatProxy.NetworkConnection;
using KcpNatProxy.SocketTransport;
using KcpSharp;

namespace KcpNatProxy.Client
{
    internal sealed class KnpPeerConnectionReceiver : IKcpNetworkApplication
    {
        private readonly int _mtu;
        private readonly IKcpBufferPool _bufferPool;

        private readonly object _lock = new();
        private KcpSocketNetworkTransport? _transport;
        private uint _serverId;
        private int _sessionId;
        private KcpSocketNetworkApplicationRegistration _transportRegistration;

        private ConcurrentDictionary<long, IKnpPeerConnectionReceiverPeerCallback> _callbacks = new();

        public int Mtu => _mtu;
        public uint ServerId => _serverId;
        public int SessionId => _sessionId;

        public KnpPeerConnectionReceiver(int mtu, IKcpBufferPool bufferPool)
        {
            _mtu = mtu;
            _bufferPool = bufferPool;
        }

        public KnpPeerConnectionReceiverTransportRegistration RegisterTransport(KcpSocketNetworkTransport transport, uint serverId, int sessionId)
        {
            lock (_lock)
            {
                if (_transport is not null)
                {
                    return default;
                }

                _transport = transport;
                _serverId = serverId;
                _sessionId = sessionId;
                _transportRegistration = transport.RegisterFallback(this);
            }

            return new KnpPeerConnectionReceiverTransportRegistration(this, transport);
        }

        public void UnregisterTransport(KcpSocketNetworkTransport transport)
        {
            bool unregistered;
            lock (_lock)
            {
                unregistered = ReferenceEquals(_transport, transport);
                if (unregistered)
                {
                    _transport = null;
                    _serverId = 0;
                    _sessionId = 0;
                    _transportRegistration.Dispose();
                    _transportRegistration = default;
                }
            }
            if (unregistered)
            {
                Clear();
            }
        }

        void IKcpNetworkApplication.SetTransportClosed() => Clear();
        ValueTask IKcpNetworkApplication.SetTransportClosedAsync()
        {
            Clear();
            return default;
        }

        private void Clear()
        {
            KnpCollectionHelper.Clear(_callbacks, c => c.OnTransportClosed());
        }

        public bool TryRegister(long accessSecret, IKnpPeerConnectionReceiverPeerCallback callback)
        {
            if (_transport is null)
            {
                return false;
            }
            if (!_callbacks.TryAdd(accessSecret, callback))
            {
                return false;
            }
            if (Volatile.Read(ref _transport) is null)
            {
                if (_callbacks.TryRemove(new KeyValuePair<long, IKnpPeerConnectionReceiverPeerCallback>(accessSecret, callback)))
                {
                    return false;
                }
            }
            return true;
        }

        public bool TryUnregister(long accessSecret, IKnpPeerConnectionReceiverPeerCallback callback)
        {
            return _callbacks.TryRemove(new KeyValuePair<long, IKnpPeerConnectionReceiverPeerCallback>(accessSecret, callback));
        }

        [SkipLocalsInit]
        public ValueTask SendProbingMessageAsync(int remoteSessionId, long accessSecret, EndPoint remoteEndPoint, CancellationToken cancellationToken)
        {
            KcpSocketNetworkTransport? transport = _transport;
            if (transport is null)
            {
                return default;
            }

            Span<byte> buffer = stackalloc byte[24];
            BinaryPrimitives.WriteUInt32BigEndian(buffer, 0xDE4B4E50);
            BinaryPrimitives.WriteUInt32BigEndian(buffer.Slice(4), _serverId);
            BinaryPrimitives.WriteInt32BigEndian(buffer.Slice(8), remoteSessionId);
            BinaryPrimitives.WriteInt32BigEndian(buffer.Slice(12), _sessionId);
            MemoryMarshal.Write<long>(buffer.Slice(16), ref accessSecret);

            return transport.QueueAndSendPacketAsync(buffer, remoteEndPoint, cancellationToken);
        }

        ValueTask IKcpNetworkApplication.InputPacketAsync(ReadOnlyMemory<byte> packet, EndPoint remoteEndPoint, CancellationToken cancellationToken)
        {
            KcpSocketNetworkTransport? transport = _transport;
            if (packet.IsEmpty || transport is null)
            {
                return default;
            }
            ReadOnlySpan<byte> packetSpan = packet.Span;

            if (packetSpan[0] == 0xde)
            {
                // probe message
                ProcessProbeMessage(transport, packetSpan, remoteEndPoint);
                return default;
            }
            else if (packetSpan[0] == 0x01)
            {
                // negotiation packet
                return ProcessNegotiationPacket(transport, packet, remoteEndPoint, cancellationToken);
            }
            return default;
        }

        private void ProcessProbeMessage(IKcpNetworkTransport transport, ReadOnlySpan<byte> packetSpan, EndPoint remoteEndPoint)
        {
            if (packetSpan.Length < 24)
            {
                return;
            }
            if (BinaryPrimitives.ReadUInt32BigEndian(packetSpan) != 0xDE4B4E50
                || BinaryPrimitives.ReadUInt32BigEndian(packetSpan.Slice(4)) != _serverId
                || BinaryPrimitives.ReadInt32BigEndian(packetSpan.Slice(8)) != _sessionId)
            {
                return;
            }
            int sessionId = BinaryPrimitives.ReadInt32BigEndian(packetSpan.Slice(12));
            long accessSecret = MemoryMarshal.Read<long>(packetSpan.Slice(16));
            if (sessionId == _sessionId)
            {
                return;
            }

            if (!_callbacks.TryGetValue(accessSecret, out IKnpPeerConnectionReceiverPeerCallback? callback))
            {
                return;
            }
            var networkConnection = new KcpNetworkConnection(transport, remoteEndPoint, new KcpNetworkConnectionOptions { Mtu = _mtu, BufferPool = _bufferPool });
            networkConnection.SetApplicationRegistration(transport.Register(remoteEndPoint, networkConnection));

            if (!callback.OnPeerProbing(sessionId, remoteEndPoint, networkConnection))
            {
                networkConnection.Dispose();
            }
        }

        private ValueTask ProcessNegotiationPacket(IKcpNetworkTransport transport, ReadOnlyMemory<byte> packet, EndPoint remoteEndPoint, CancellationToken cancellationToken)
        {
            ReadOnlySpan<byte> packetSpan = packet.Span;
            if (packetSpan.Length < 8)
            {
                return default;
            }
            // check negotiation frame
            int length = BinaryPrimitives.ReadUInt16BigEndian(packetSpan.Slice(2));
            if ((packetSpan.Length - 4) < length)
            {
                return default;
            }
            length = BinaryPrimitives.ReadUInt16BigEndian(packetSpan.Slice(6));
            if ((packetSpan.Length - 8) < length)
            {
                return default;
            }
            packetSpan = packetSpan.Slice(8, length);

            // check peer connection request
            if (packetSpan.Length < 26)
            {
                return default;
            }
            if (BinaryPrimitives.ReadUInt32BigEndian(packetSpan) != 0xDE4B4E50
                || BinaryPrimitives.ReadUInt32BigEndian(packetSpan.Slice(4)) != _serverId
                || BinaryPrimitives.ReadInt32BigEndian(packetSpan.Slice(8)) != _sessionId)
            {
                return default;
            }
            int sessionId = BinaryPrimitives.ReadInt32BigEndian(packetSpan.Slice(12));
            long accessSecret = MemoryMarshal.Read<long>(packetSpan.Slice(16));
            if (sessionId == _sessionId)
            {
                return default;
            }

            if (!_callbacks.TryGetValue(accessSecret, out IKnpPeerConnectionReceiverPeerCallback? callback))
            {
                return default;
            }

            var networkConnection = new KcpNetworkConnection(transport, remoteEndPoint, new KcpNetworkConnectionOptions { Mtu = _mtu, BufferPool = _bufferPool });
            networkConnection.SetApplicationRegistration(transport.Register(remoteEndPoint, networkConnection));

            if (!callback.OnPeerProbing(sessionId, remoteEndPoint, networkConnection))
            {
                networkConnection.Dispose();
                return default;
            }

            return networkConnection.InputPacketAsync(packet, cancellationToken);
        }
    }

    internal readonly struct KnpPeerConnectionReceiverTransportRegistration : IDisposable
    {
        private readonly KnpPeerConnectionReceiver _receiver;
        private readonly KcpSocketNetworkTransport _transport;

        public KnpPeerConnectionReceiverTransportRegistration(KnpPeerConnectionReceiver receiver, KcpSocketNetworkTransport transport)
        {
            _receiver = receiver;
            _transport = transport;
        }

        public void Dispose()
        {
            if (_receiver is not null)
            {
                _receiver.UnregisterTransport(_transport);
            }
        }
    }

}
