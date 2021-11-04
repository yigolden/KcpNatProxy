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
    internal sealed class KnpVirtualBusServiceBindingControlChannel : IKcpTransport, IKnpForwardSession, IThreadPoolWorkItem, IDisposable
    {
        private readonly KnpVirtualBusServiceBinding _serviceBinding;
        private readonly ILogger _logger;
        private readonly KcpConversation _conversation;

        private readonly object _stateChangeLock = new();
        private CancellationTokenSource? _cts;
        private byte[]? _bindingSessionIdBytes;
        private bool _disposed;

        public KnpVirtualBusServiceBindingControlChannel(KnpVirtualBusServiceBinding serviceBinding, int mtu, ILogger logger)
        {
            _serviceBinding = serviceBinding;
            _logger = logger;
            _conversation = new KcpConversation(this, new KcpConversationOptions { Mtu = mtu, PreBufferSize = 8, BufferPool = serviceBinding.BufferPool });
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

                byte[] bindingSessionIdBytes = new byte[8];
                BinaryPrimitives.WriteInt32BigEndian(bindingSessionIdBytes, _serviceBinding.BindingId);
                _bindingSessionIdBytes = bindingSessionIdBytes;
            }

            ThreadPool.UnsafeQueueUserWorkItem(this, false);
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

                CancellationTokenSource? cts = _cts;
                if (cts is not null)
                {
                    cts.Cancel();
                    cts.Dispose();
                    _cts = null;
                }

                _bindingSessionIdBytes = null;
            }
        }

        public bool IsExpired(long tick) => _disposed;

        async void IThreadPoolWorkItem.Execute()
        {
            CancellationToken cancellationToken;
            lock (_stateChangeLock)
            {
                if (_cts is null)
                {
                    return;
                }
                cancellationToken = _cts.Token;
            }
            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    if (!await _conversation.WaitForSendQueueAvailableSpaceAsync(0, 1, cancellationToken).ConfigureAwait(false))
                    {
                        return;
                    }
                    KcpConversationReceiveResult result = await _conversation.WaitToReceiveAsync(cancellationToken).ConfigureAwait(false);
                    if (result.BytesReceived == 0 || result.BytesReceived > 256)
                    {
                        return;
                    }

                    if (!ProcessPacket(_conversation, result.BytesReceived))
                    {
                        break;
                    }
                }
                if (!cancellationToken.IsCancellationRequested)
                {
                    await _conversation.FlushAsync(cancellationToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                // Do nothing
            }
            catch (Exception ex)
            {
                Log.LogServerUnhandledException(_logger, ex);
                Dispose();
            }
        }

        private static void SendReset(KcpConversation conversation)
        {
            byte b = 0xff;
            conversation.TrySend(MemoryMarshal.CreateReadOnlySpan(ref b, 1));
        }

        [SkipLocalsInit]
        private bool ProcessPacket(KcpConversation conversation, int bytesReceived)
        {
            Debug.Assert(bytesReceived != 0 && bytesReceived <= 256);
            Span<byte> buffer = stackalloc byte[256];
            conversation.TryReceive(buffer, out KcpConversationReceiveResult result);
            if (result.BytesReceived != bytesReceived)
            {
                return false;
            }

            int commandType = buffer[0];
            if (commandType == 1)
            {
                // bind provider
                bool succeeded = ProcessBindProviderCommand(buffer.Slice(0, bytesReceived));
                if (!succeeded)
                {
                    SendReset(conversation);
                }
                return succeeded;
            }
            else if (commandType == 2)
            {
                // query provider
                bool succeeded = ProcessQueryProviderCommand(buffer.Slice(0, bytesReceived));
                if (!succeeded)
                {
                    SendReset(conversation);
                }
                return succeeded;
            }
            else if (commandType == 3)
            {
                // p2p connect
                bool succeeded = ProcessConnectPeerToPeerCommand(buffer.Slice(0, bytesReceived));
                if (!succeeded)
                {
                    SendReset(conversation);
                }
                return succeeded;

            }
            else if (commandType == 0xff)
            {
                return false;
            }
            else
            {
                SendReset(conversation);
                return false;
            }
        }

        private bool ProcessBindProviderCommand(ReadOnlySpan<byte> buffer)
        {
            Debug.Assert(buffer[0] == 1);
            if (buffer.Length < 4)
            {
                return false;
            }

            var serviceType = (KnpServiceType)buffer[1];
            if (serviceType != KnpServiceType.Tcp && serviceType != KnpServiceType.Udp)
            {
                return false;
            }

            if (!KnpParseHelper.TryParseName(buffer.Slice(2), out int bytesRead, out string? name))
            {
                return false;
            }
            bytesRead += 2;

            KnpVirtualBusProviderInfo? provider = _serviceBinding.TryAddProvider(name, serviceType, buffer.Slice(bytesRead));
            if (provider is null)
            {
                return SendBindProviderResult(_conversation, null);
            }

            if (_cts is null)
            {
                _serviceBinding.RemoveProvider(provider);
                return false;
            }

            return SendBindProviderResult(_conversation, provider.BindingId);
        }

        [SkipLocalsInit]
        private bool SendBindProviderResult(KcpConversation conversation, int? bindingId)
        {
            Span<byte> buffer = stackalloc byte[6];
            if (bindingId.HasValue)
            {
                buffer[0] = 0;
                buffer[1] = 0;
                BinaryPrimitives.WriteInt32BigEndian(buffer.Slice(2), bindingId.GetValueOrDefault());
                return conversation.TrySend(buffer.Slice(0, 6));
            }
            else
            {
                buffer[0] = 1;
                buffer[1] = 3;
                return conversation.TrySend(buffer.Slice(0, 2));
            }
        }

        private bool ProcessQueryProviderCommand(ReadOnlySpan<byte> buffer)
        {
            Debug.Assert(buffer[0] == 2);
            if (buffer.Length < 4)
            {
                return false;
            }

            var serviceType = (KnpServiceType)buffer[1];
            if (serviceType != KnpServiceType.Tcp && serviceType != KnpServiceType.Udp)
            {
                return false;
            }

            if (!KnpParseHelper.TryParseName(buffer.Slice(2), out int bytesRead, out string? name))
            {
                return false;
            }
            bytesRead += 2;

            KnpVirtualBusProviderInfo? provider = _serviceBinding.QueryProvider(name, serviceType);
            if (provider is null)
            {
                return SendQueryProviderResult(_conversation, null);
            }

            if (_cts is null)
            {
                return false;
            }

            if (bytesRead < buffer.Length)
            {
                _serviceBinding.ForwardProviderParameter(provider, buffer.Slice(bytesRead));
            }

            return SendQueryProviderResult(_conversation, provider);
        }

        [SkipLocalsInit]
        private bool SendQueryProviderResult(KcpConversation conversation, KnpVirtualBusProviderInfo? provider)
        {
            Span<byte> buffer = stackalloc byte[18];
            if (provider is not null)
            {
                buffer[0] = 0;
                buffer[1] = 0;
                BinaryPrimitives.WriteInt32BigEndian(buffer.Slice(2), provider.SessionId);
                BinaryPrimitives.WriteInt32BigEndian(buffer.Slice(6), provider.BindingId);
                byte[] parameters = provider.Parameters;
                if (parameters.Length <= 8)
                {
                    parameters.CopyTo(buffer.Slice(10));
                    return conversation.TrySend(buffer.Slice(0, 10 + parameters.Length));
                }
                return conversation.TrySend(buffer.Slice(0, 10));
            }
            else
            {
                buffer[0] = 1;
                buffer[1] = 3;
                return conversation.TrySend(buffer.Slice(0, 2));
            }
        }

        [SkipLocalsInit]
        private bool ProcessConnectPeerToPeerCommand(ReadOnlySpan<byte> buffer)
        {
            Debug.Assert(buffer[0] == 3);
            if (buffer.Length < 5)
            {
                return false;
            }
            if (_cts is null)
            {
                return false;
            }

            Span<byte> sendBuffer = stackalloc byte[28];
            int sessionId = BinaryPrimitives.ReadInt32BigEndian(buffer.Slice(1));
            if (!_serviceBinding.ForwardPeerToPeerRequest(sessionId, out long accessSecret, out IPEndPoint? peerEndPoint, out byte errorCode))
            {
                sendBuffer[0] = 1;
                sendBuffer[1] = errorCode;
                return _conversation.TrySend(sendBuffer.Slice(0, 2));
            }
            else if (peerEndPoint.AddressFamily != AddressFamily.InterNetworkV6 && peerEndPoint.AddressFamily != AddressFamily.InterNetwork)
            {
                sendBuffer[0] = 1;
                sendBuffer[1] = 3;
                return _conversation.TrySend(sendBuffer.Slice(0, 2));
            }
            else
            {
                sendBuffer[0] = 0;
                sendBuffer[1] = 0;
                MemoryMarshal.Write(sendBuffer.Slice(2), ref accessSecret);
                int bytesWritten;
                if (peerEndPoint.AddressFamily == AddressFamily.InterNetworkV6)
                {
                    peerEndPoint.Address.TryWriteBytes(sendBuffer.Slice(10), out bytesWritten);
                    Debug.Assert(bytesWritten == 16);
                }
                else
                {
                    peerEndPoint.Address.MapToIPv6().TryWriteBytes(sendBuffer.Slice(10), out bytesWritten);
                    Debug.Assert(bytesWritten == 16);
                }
                bytesWritten += 10;
                BinaryPrimitives.WriteUInt16BigEndian(sendBuffer.Slice(bytesWritten), (ushort)peerEndPoint.Port);
                bytesWritten += 2;
                return _conversation.TrySend(sendBuffer.Slice(0, 28));
            }
        }

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

    }
}
