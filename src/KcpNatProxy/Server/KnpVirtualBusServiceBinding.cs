using System;
using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Sockets;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using KcpNatProxy.Server;
using KcpSharp;

namespace KcpNatProxy
{
    internal sealed class KnpVirtualBusServiceBinding : IKnpServiceBinding, IKnpForwardHost
    {
        private readonly KnpVirtualBusService _virtualBusService;
        private readonly IKnpConnectionHost _host;
        private readonly int _sessionId;
        private readonly KnpRentedInt32 _bindingId;
        private long _lastResetTime;

        private readonly object _stateChangeLock = new();
        private KnpVirtualBusServiceBindingControlChannel? _controlChannel;
        private KnpVirtualBusServiceBindingNotificationChannel? _notificationChannel;
        private byte[]? _bindingIdBytes;
        private byte[]? _sessionIdBytes;
        private bool _disposed;

        public string ServiceName => _virtualBusService.ServiceName;
        public int BindingId => _bindingId.Value;
        public int Mss => _host.Mtu - 8;
        public int SessionId => _sessionId;
        public IKcpBufferPool BufferPool => _host.BufferPool;
        public EndPoint? RemoteEndPoint => _host.RemoteEndPoint;

        public KnpVirtualBusServiceBinding(KnpVirtualBusService virtualBusService, IKnpConnectionHost host, KnpRentedInt32 bindingId)
        {
            _virtualBusService = virtualBusService;
            _host = host;
            _bindingId = bindingId;

            if (!host.TryGetSessionId(out int sessionId, out _))
            {
                throw new NotSupportedException();
            }
            _sessionId = sessionId;
            _lastResetTime = DateTime.UtcNow.ToBinary();
        }

        public KnpVirtualBusServiceBinding? FindOtherClient(int sessionId)
            => _disposed ? null : _virtualBusService.FindClient(sessionId);

        public KnpVirtualBusServiceBindingNotificationChannel? GetNotificationChannel()
            => _disposed ? null : _notificationChannel;


        [SkipLocalsInit]
        public void Start()
        {
            KnpVirtualBusServiceBindingControlChannel controlChannel;
            KnpVirtualBusServiceBindingNotificationChannel notificationChannel;
            lock (_stateChangeLock)
            {
                if (_disposed || _controlChannel is not null)
                {
                    return;
                }

                _controlChannel = controlChannel = new KnpVirtualBusServiceBindingControlChannel(this, _host.Mtu, _host.Logger);
                _notificationChannel = notificationChannel = new KnpVirtualBusServiceBindingNotificationChannel(this, _host.Mtu, _host.Logger);

                byte[] bindingIdBytes = new byte[4];
                byte[] sessionIdBytes = new byte[4];
                BinaryPrimitives.WriteInt32BigEndian(bindingIdBytes, _bindingId.Value);
                BinaryPrimitives.WriteInt32BigEndian(sessionIdBytes, _sessionId);
                _bindingIdBytes = bindingIdBytes;
                _sessionIdBytes = sessionIdBytes;
            }

            controlChannel.Start();
            notificationChannel.Start();

            Span<byte> buffer = stackalloc byte[8];
            _bindingId.WriteBigEndian(buffer);

            buffer.Slice(4).Clear();
            _host.TryRegister(MemoryMarshal.Read<long>(buffer), controlChannel);

            BinaryPrimitives.WriteInt32BigEndian(buffer.Slice(4), -1);
            _host.TryRegister(MemoryMarshal.Read<long>(buffer), notificationChannel);

            if (_disposed)
            {
                buffer.Slice(4).Clear();
                _host.TryUnregister(MemoryMarshal.Read<long>(buffer), controlChannel);

                BinaryPrimitives.WriteInt32BigEndian(buffer.Slice(4), -1);
                _host.TryRegister(MemoryMarshal.Read<long>(buffer), notificationChannel);

                controlChannel.Dispose();
                notificationChannel.Dispose();
            }
        }

        [SkipLocalsInit]
        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }
            KnpVirtualBusServiceBindingControlChannel? controlChannel;
            KnpVirtualBusServiceBindingNotificationChannel? notificationChannel;
            lock (_stateChangeLock)
            {
                if (_disposed)
                {
                    return;
                }
                _disposed = true;

                controlChannel = _controlChannel;
                _controlChannel = null;

                notificationChannel = _notificationChannel;
                _notificationChannel = null;

                _bindingIdBytes = null;
                _sessionIdBytes = null;
            }

            Span<byte> buffer = stackalloc byte[8];
            _bindingId.WriteBigEndian(buffer);

            if (controlChannel is not null)
            {
                controlChannel.Dispose();

                buffer.Slice(4).Clear();
                _host.TryUnregister(MemoryMarshal.Read<long>(buffer), controlChannel);
            }

            if (notificationChannel is not null)
            {
                notificationChannel.Dispose();

                BinaryPrimitives.WriteInt32BigEndian(buffer.Slice(4), -1);
                _host.TryUnregister(MemoryMarshal.Read<long>(buffer), notificationChannel);
            }

            _virtualBusService.UnregisterBinding(this);
            _bindingId.Dispose();
        }

        public void ForwardProviderParameter(KnpVirtualBusProviderInfo provider, ReadOnlySpan<byte> parameters)
        {
            if (parameters.IsEmpty || _disposed)
            {
                return;
            }

            KnpVirtualBusServiceBindingNotificationChannel? peerNotificationChannel = _virtualBusService.FindClient(provider.SessionId)?.GetNotificationChannel();
            if (peerNotificationChannel is not null)
            {
                peerNotificationChannel.SendProviderParameters(_sessionId, provider.BindingId, parameters);
            }
        }

        public bool ForwardPeerToPeerRequest(int targetSessionId, out long accessSecret, [NotNullWhen(true)] out IPEndPoint? peerEndPoint, out byte errorCode)
        {
            if (_virtualBusService.RelayType != KnpVirtualBusRelayType.Never && _virtualBusService.RelayType != KnpVirtualBusRelayType.Mixed)
            {
                accessSecret = default;
                peerEndPoint = null;
                errorCode = 1; // service not supported
                return false;
            }
            if (_disposed)
            {
                accessSecret = default;
                peerEndPoint = null;
                errorCode = 2; // peer client not supported
                return false;
            }
            KnpVirtualBusServiceBinding? peerClient = _virtualBusService.FindClient(targetSessionId);
            if (peerClient is null)
            {
                accessSecret = default;
                peerEndPoint = null;
                errorCode = 2; // peer client not supported
                return false;
            }
            KnpVirtualBusServiceBindingNotificationChannel? peerNotificationChannel = peerClient.GetNotificationChannel();
            if (peerNotificationChannel is null)
            {
                accessSecret = default;
                peerEndPoint = null;
                errorCode = 2; // peer client not supported
                return false;
            }
            if (peerClient.RemoteEndPoint is not IPEndPoint ipep)
            {
                accessSecret = default;
                peerEndPoint = null;
                errorCode = 3; // address mode not supported
                return false;
            }

            accessSecret = (uint)_sessionId;
            if ((uint)targetSessionId > accessSecret)
            {
                accessSecret |= (long)(((ulong)(uint)targetSessionId) << 32);
            }
            else
            {
                accessSecret = (long)(((ulong)accessSecret << 32) | (uint)targetSessionId);
            }
            accessSecret ^= (long)(((ulong)BitOperations.RotateLeft((uint)_bindingId.Value, 5) << 32) | BitOperations.RotateLeft((uint)_bindingId.Value, 13));
            peerEndPoint = ipep;
            if (_host.RemoteEndPoint is not IPEndPoint ipep2)
            {
                errorCode = 3; // address mode not supported
                return false;
            }
            if (ipep.AddressFamily != AddressFamily.InterNetworkV6 && ipep.AddressFamily == AddressFamily.InterNetwork)
            {
                errorCode = 3; // address mode not supported
                return false;
            }
            if (ipep2.AddressFamily != AddressFamily.InterNetworkV6 && ipep2.AddressFamily == AddressFamily.InterNetwork)
            {
                errorCode = 3; // address mode not supported
                return false;
            }

            if (!peerNotificationChannel.SendPeerToPeerConnectionRequest(_sessionId, accessSecret, ipep2))
            {
                errorCode = 4; // failed to send connect request
                return false;
            }

            errorCode = 0;
            return true;
        }

        ValueTask IKnpServiceBinding.InputPacketAsync(ReadOnlyMemory<byte> packet, CancellationToken cancellationToken)
        {
            if (packet.Length < 8)
            {
                return default;
            }

            int sessionId = BinaryPrimitives.ReadInt32BigEndian(packet.Span.Slice(4));
            if (sessionId <= 0)
            {
                // reserved channels
                return default;
            }

            if (!_virtualBusService.TryGetSessionForForward(sessionId, out KnpVirtualBusServiceBinding? target))
            {
                return SendBackResetAsync(packet.Slice(0, 8), cancellationToken);
            }

            byte[]? sessionIdBytes = _sessionIdBytes;
            if (sessionIdBytes is not null)
            {
                return target.ForwardAsync(packet.Slice(8), sessionIdBytes, cancellationToken);
            }

            return default;
        }

        private ValueTask SendBackResetAsync(ReadOnlyMemory<byte> targetBindingSessionId, CancellationToken cancellationToken)
        {
            DateTime lastSendTime = DateTime.FromBinary(Interlocked.Read(ref _lastResetTime));
            DateTime utcNow = DateTime.UtcNow;
            if (utcNow.Subtract(TimeSpan.FromMilliseconds(400)) < lastSendTime)
            {
                return default;
            }
            Interlocked.Exchange(ref _lastResetTime, utcNow.ToBinary());

            using var bufferList = KcpRentedBufferList.Allocate(ConstantByteArrayCache.FFByte);
            return _host.SendAsync(bufferList.AddPreBuffer(targetBindingSessionId), cancellationToken);
        }

        private ValueTask ForwardAsync(ReadOnlyMemory<byte> packet, byte[] sessionIdBytes, CancellationToken cancellationToken)
        {
            if (packet.IsEmpty)
            {
                return default;
            }
            using var bufferList = KcpRentedBufferList.Allocate(packet);
            return _host.SendAsync(bufferList.AddPreBuffer(sessionIdBytes).AddPreBuffer(_bindingIdBytes), cancellationToken);
        }

        public ValueTask ForwardBackAsync(KcpBufferList bufferList, CancellationToken cancellationToken)
            => _host.SendAsync(bufferList, cancellationToken);
        void IKnpForwardHost.NotifySessionClosed(int forwardId, IKnpForwardSession session) { }

        internal KnpVirtualBusProviderInfo? TryAddProvider(string name, KnpServiceType serviceType, ReadOnlySpan<byte> parameters)
            => _virtualBusService.TryAddProvider(_sessionId, name, serviceType, parameters);

        internal void RemoveProvider(KnpVirtualBusProviderInfo provider)
            => _virtualBusService.RemoveProvider(provider);

        internal KnpVirtualBusProviderInfo? QueryProvider(string name, KnpServiceType serviceType)
            => _virtualBusService.QueryProvider(name, serviceType);
    }
}
