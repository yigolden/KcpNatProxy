using System;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using KcpSharp;
using Microsoft.Extensions.Logging;

namespace KcpNatProxy.Server
{
    internal sealed class KnpVirtualBusServiceBindingNotificationChannel : IKcpTransport, IKnpForwardSession, IDisposable
    {
        private readonly KnpVirtualBusServiceBinding _serviceBinding;
        private readonly ILogger _logger;
        private readonly KcpConversation _conversation;

        private readonly object _stateChangeLock = new();
        private byte[]? _bindingSessionIdBytes;
        private bool _disposed;

        public KnpVirtualBusServiceBindingNotificationChannel(KnpVirtualBusServiceBinding serviceBinding, int mtu, ILogger logger)
        {
            _serviceBinding = serviceBinding;
            _logger = logger;
            _conversation = new KcpConversation(this, new KcpConversationOptions
            {
                Mtu = mtu,
                PreBufferSize = 8,
                ReceiveWindow = 1, // we don't want to receive message
                BufferPool = serviceBinding.BufferPool
            });
        }

        public void Start()
        {
            lock (_stateChangeLock)
            {
                if (_disposed || _bindingSessionIdBytes is not null)
                {
                    return;
                }

                byte[] bindingSessionIdBytes = new byte[8];
                BinaryPrimitives.WriteInt32BigEndian(bindingSessionIdBytes, _serviceBinding.BindingId);
                BinaryPrimitives.WriteInt32BigEndian(bindingSessionIdBytes.AsSpan(4), -1);
                _bindingSessionIdBytes = bindingSessionIdBytes;
            }
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

                _conversation.Dispose();

                _bindingSessionIdBytes = null;
            }
        }

        public bool IsExpired(DateTime utcNow) => _disposed;

        public ValueTask InputPacketAsync(ReadOnlyMemory<byte> packet, CancellationToken cancellationToken)
            => _conversation.InputPakcetAsync(packet, cancellationToken);

        ValueTask IKcpTransport.SendPacketAsync(Memory<byte> packet, CancellationToken cancellationToken)
        {
            Debug.Assert(packet.Length >= 8);
            byte[]? bindingSessionIdBytes = _bindingSessionIdBytes;
            if (bindingSessionIdBytes is null)
            {
                return default;
            }
            bindingSessionIdBytes.CopyTo(packet);

            using var bufferList = KcpRentedBufferList.Allocate(packet);
            return _serviceBinding.ForwardBackAsync(bufferList, cancellationToken);
        }

        [SkipLocalsInit]
        public void SendProviderParameters(int sessionId, int bindingId, ReadOnlySpan<byte> parameters)
        {
            if (_disposed || parameters.Length > 8)
            {
                return;
            }

            Span<byte> buffer = stackalloc byte[20];
            buffer[0] = 1; // type
            BinaryPrimitives.WriteInt32BigEndian(buffer.Slice(1), sessionId);
            BinaryPrimitives.WriteInt32BigEndian(buffer.Slice(5), bindingId);
            parameters.CopyTo(buffer.Slice(9));

            _conversation.TrySend(buffer.Slice(0, 9 + parameters.Length));
        }

        [SkipLocalsInit]
        public bool SendPeerToPeerConnectionRequest(int sessionId, long accessKey, IPEndPoint peerEndPoint)
        {
            if (_disposed)
            {
                return false;
            }

            IPAddress? address = peerEndPoint.Address;
            Debug.Assert(address.AddressFamily == AddressFamily.InterNetworkV6 || address.AddressFamily == AddressFamily.InterNetwork);

            int bytesWritten;
            Span<byte> buffer = stackalloc byte[32];
            buffer[0] = 2; // type
            BinaryPrimitives.WriteInt32BigEndian(buffer.Slice(1), sessionId);
            MemoryMarshal.Write(buffer.Slice(5), ref accessKey);
            if (address.AddressFamily == AddressFamily.InterNetworkV6)
            {
                address.TryWriteBytes(buffer.Slice(13), out bytesWritten);
                Debug.Assert(bytesWritten == 16);
            }
            else if (address.AddressFamily == AddressFamily.InterNetwork)
            {
                address.MapToIPv6().TryWriteBytes(buffer.Slice(13), out bytesWritten);
                Debug.Assert(bytesWritten == 16);
            }
            else
            {
                return false;
            }
            bytesWritten += 13;
            BinaryPrimitives.WriteUInt16BigEndian(buffer.Slice(bytesWritten), (ushort)peerEndPoint.Port);
            bytesWritten += 2;

            return _conversation.TrySend(buffer.Slice(0, bytesWritten));
        }
    }
}
