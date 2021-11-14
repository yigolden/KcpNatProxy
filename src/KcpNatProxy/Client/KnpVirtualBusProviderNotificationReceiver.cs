using System;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using KcpSharp;

namespace KcpNatProxy.Client
{
    internal sealed class KnpVirtualBusProviderNotificationReceiver : IKnpForwardSession, IKcpTransport, IThreadPoolWorkItem, IDisposable
    {
        private readonly KnpVirtualBusProvider _provider;
        private readonly IKnpConnectionHost _host;
        private readonly int _bindingId;

        private readonly KcpConversation _conversation;
        private readonly byte[] _bindingSessionIdBytes;

        private object _stateChangeLock = new();
        private CancellationTokenSource? _cts;
        private bool _registered;
        private bool _disposed;

        public KnpVirtualBusProviderNotificationReceiver(KnpVirtualBusProvider provider, IKnpConnectionHost host, int bindingId)
        {
            _provider = provider;
            _host = host;
            _bindingId = bindingId;

            _conversation = new KcpConversation(this, new KcpConversationOptions { Mtu = host.Mtu, PreBufferSize = 8, BufferPool = host.BufferPool });
            _bindingSessionIdBytes = new byte[8];
            BinaryPrimitives.WriteInt32BigEndian(_bindingSessionIdBytes, bindingId);
            BinaryPrimitives.WriteInt32BigEndian(_bindingSessionIdBytes.AsSpan(4), -1);
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
                _registered = _host.TryRegister(MemoryMarshal.Read<long>(_bindingSessionIdBytes), this);
            }

            ThreadPool.UnsafeQueueUserWorkItem(this, false);
        }
        public bool IsExpired(long tick) => _disposed;
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

                CancellationTokenSource? cts = _cts;
                if (cts is not null)
                {
                    cts.Cancel();
                    cts.Dispose();
                    _cts = null;
                }

                _conversation.Dispose();
                if (_registered)
                {
                    _registered = false;
                    _host.TryUnregister(MemoryMarshal.Read<long>(_bindingSessionIdBytes), this);
                }
            }
        }

        ValueTask IKnpForwardSession.InputPacketAsync(ReadOnlyMemory<byte> packet, CancellationToken cancellationToken)
        {
            if (_disposed)
            {
                return default;
            }
            return _conversation.InputPakcetAsync(packet, cancellationToken);
        }

        ValueTask IKcpTransport.SendPacketAsync(Memory<byte> packet, CancellationToken cancellationToken)
        {
            Debug.Assert(packet.Length >= 8);
            if (_disposed)
            {
                return default;
            }

            _bindingSessionIdBytes.CopyTo(packet);

            using var bufferList = KcpRentedBufferList.Allocate(packet);
            return _host.SendAsync(bufferList, cancellationToken);
        }


        async void IThreadPoolWorkItem.Execute()
        {
            CancellationToken cancellationToken;
            lock (_stateChangeLock)
            {
                if (_cts is null || _cts.IsCancellationRequested)
                {
                    return;
                }
                cancellationToken = _cts.Token;
            }

            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    KcpConversationReceiveResult result = await _conversation.WaitToReceiveAsync(cancellationToken).ConfigureAwait(false);
                    if (result.TransportClosed)
                    {
                        break;
                    }
                    ProcessNotification(_conversation, result);
                }
            }
            catch (OperationCanceledException)
            {
                // Ignore
            }
            catch (Exception ex)
            {
                Log.LogClientUnhandledException(_host.Logger, ex);
            }
        }

        [SkipLocalsInit]
        private void ProcessNotification(KcpConversation conversation, KcpConversationReceiveResult result)
        {
            if (result.BytesReceived > 256)
            {
                conversation.SetTransportClosed();
                return;
            }

            Span<byte> buffer = stackalloc byte[256];
            if (!conversation.TryReceive(buffer, out result) || result.BytesReceived == 0)
            {
                return;
            }

            int type = buffer[0];
            if (type == 1)
            {
                ProcessProviderParameters(buffer.Slice(1, result.BytesReceived - 1));
            }
            else if (type == 2)
            {
                ProcessPeerToPeerConnectionReuqest(buffer.Slice(1, result.BytesReceived - 1));
            }
        }

        private void ProcessProviderParameters(ReadOnlySpan<byte> data)
        {
            if (data.Length <= 8)
            {
                return;
            }

            int sessionId = BinaryPrimitives.ReadInt32BigEndian(data);
            int bindingId = BinaryPrimitives.ReadInt32BigEndian(data.Slice(4));

            _provider.NotifyProviderParameters(sessionId, bindingId, data.Slice(8));
        }

        private void ProcessPeerToPeerConnectionReuqest(ReadOnlySpan<byte> data)
        {
            if (data.Length < 30)
            {
                return;
            }

            int sessionId = BinaryPrimitives.ReadInt32BigEndian(data);
            long accessSecret = MemoryMarshal.Read<long>(data.Slice(4));
            ReadOnlySpan<byte> ipep = data.Slice(12, 18);

            _provider.NotifyPeerToPeerConnection(sessionId, accessSecret, ipep);
        }
    }
}
