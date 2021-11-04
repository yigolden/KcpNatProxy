using System;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using KcpNatProxy.NetworkConnection;
using KcpSharp;
using Microsoft.Extensions.Logging;

namespace KcpNatProxy.Server
{
    internal sealed class KnpClientSession : IKcpNetworkConnectionCallback, IKnpConnectionHost, IKcpTransport, IThreadPoolWorkItem, IDisposable
    {
        private readonly KnpServer _server;
        private readonly KcpNetworkConnection _connection;
        private readonly ILogger _logger;
        private readonly KnpRentedInt32 _sessionId;
        private readonly byte[] _sessionIdBytes;
        private CancellationTokenSource? _cts;
        private KcpConversation? _controlChannel;
        private bool _unregistered;

        private readonly ConcurrentDictionary<int, IKnpServiceBinding> _bindings = new();
        private readonly ConcurrentDictionary<long, IKnpForwardSession> _forwardSessions = new();

        IKcpBufferPool IKnpConnectionHost.BufferPool => _server.BufferPool;
        ILogger IKnpConnectionHost.Logger => _logger;
        int IKnpConnectionHost.Mtu => _connection.Mss;
        EndPoint? IKnpConnectionHost.RemoteEndPoint => _connection.RemoteEndPoint;

        public KnpClientSession(KnpServer server, KcpNetworkConnection connection, KnpRentedInt32 sessionId, ILogger logger)
        {
            _server = server;
            _connection = connection;
            _logger = logger;
            _sessionId = sessionId;
            _sessionIdBytes = new byte[4];
            BinaryPrimitives.WriteInt32BigEndian(_sessionIdBytes, sessionId.Value);
        }

        public bool TryGetSessionId(out int sessionId, [NotNullWhen(true)] out byte[]? sessionIdBytes)
        {
            sessionId = _sessionId.Value;
            sessionIdBytes = _sessionIdBytes;
            return true;
        }

        public void Start()
        {
            _cts = new CancellationTokenSource();
            _ = Task.Run(() => RunAsync(_cts.Token));
        }

        public void Dispose()
        {
            CancellationTokenSource? cts = Interlocked.Exchange(ref _cts, null);
            if (cts is not null)
            {
                cts.Cancel();
                cts.Dispose();

                if (_controlChannel is null)
                {
                    Log.LogServerSessionClosedUnauthenticated(_logger, _sessionId.Value);
                }
                else
                {
                    _controlChannel.Dispose();
                    Log.LogServerSessionClosed(_logger, _sessionId.Value);
                    _controlChannel = null;
                }

                _connection.Dispose();

                while (!_bindings.IsEmpty)
                {
                    foreach (KeyValuePair<int, IKnpServiceBinding> item in _bindings)
                    {
                        if (_bindings.TryRemove(item))
                        {
                            item.Value.Dispose();
                            Log.LogServerBindingDestroyed(_logger, item.Value.ServiceName, _sessionId.Value);
                        }
                    }
                }
                KnpCollectionHelper.ClearAndDispose(_forwardSessions);

                _sessionId.Dispose();
            }
        }

        private async Task RunAsync(CancellationToken cancellationToken)
        {
            Log.LogServerConnectionAccepted(_logger, _connection.RemoteEndPoint, _sessionId.Value);
            using (_connection.Register(this))
            {
                // negotiate
                if (!await NegotiateAsync(cancellationToken).ConfigureAwait(false))
                {
                    return;
                }

                if (!_server.RegisterClientSession(_sessionId.Value, this))
                {
                    await _connection.SetTransportClosedAsync().ConfigureAwait(false);
                    return;
                }

                ThreadPool.UnsafeQueueUserWorkItem(this, false);

                // start processing control channel
                _controlChannel = new KcpConversation(this, new KcpConversationOptions { Mtu = _connection.Mtu - KcpNetworkConnection.PreBufferSize - 4, BufferPool = _server.BufferPool });
                _controlChannel.SetExceptionHandler((ex, _, state) => Log.LogServerUnhandledException((ILogger?)state!, ex), _logger);

                try
                {
                    await ReceiveAndProcessCommandsAsync(_controlChannel, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // Ignore
                }

                await _connection.SetTransportClosedAsync().ConfigureAwait(false);
            }
        }

        private async Task<bool> NegotiateAsync(CancellationToken cancellationToken)
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(20));
            try
            {
                if (!await _connection.NegotiateAsync(_server.CreateAuthenticator(_sessionId.Value), cts.Token).ConfigureAwait(false))
                {
                    Log.LogServerSessionAuthenticationFailed(_logger, _connection.RemoteEndPoint);
                    return false;
                }
            }
            catch (OperationCanceledException)
            {
                if (!cancellationToken.IsCancellationRequested)
                {
                    Log.LogServerSessionAuthenticationTimeout(_logger, _connection.RemoteEndPoint);
                    await _connection.SetTransportClosedAsync().ConfigureAwait(false);
                }
                return false;
            }

            _connection.SetupKeepAlive(TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(40));

            Log.LogServerSessionAuthenticated(_logger, _connection.RemoteEndPoint, _sessionId.Value);
            return true;
        }

        private async Task ReceiveAndProcessCommandsAsync(KcpConversation conversation, CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                if (!await conversation.WaitForSendQueueAvailableSpaceAsync(0, 1, cancellationToken).ConfigureAwait(false))
                {
                    return;
                }
                KcpConversationReceiveResult result = await conversation.WaitToReceiveAsync(cancellationToken).ConfigureAwait(false);
                if (result.TransportClosed || result.BytesReceived > 256)
                {
                    return;
                }
                ProcessCommand(conversation, result.BytesReceived);

            }
        }

        [SkipLocalsInit]
        private void ProcessCommand(KcpConversation conversation, int bytesReceived)
        {
            Span<byte> buffer = stackalloc byte[256];

            // parse command
            if (!conversation.TryReceive(buffer, out KcpConversationReceiveResult result) || result.BytesReceived == 0)
            {
                Log.LogServerRequestInvalid(_logger, _sessionId.Value);
                WriteErrorCommandResponse(conversation, 255); // 255 - invalid request
                return;
            }
            int command = buffer[0];
            if (command == 1)
            {
                // bind provider
                ProcessBindProviderCommand(conversation, buffer.Slice(0, result.BytesReceived));
            }
            else if (command == 2)
            {
                // bind service bus
                ProcessBindVirtualBusCommand(conversation, buffer.Slice(0, result.BytesReceived));
            }
            else
            {
                Log.LogServerRequestUnsupportedCommand(_logger, _sessionId.Value);
                WriteErrorCommandResponse(conversation, 1); // 1 - unsupported command
            }
        }

        private void ProcessBindProviderCommand(KcpConversation conversation, ReadOnlySpan<byte> buffer)
        {
            Debug.Assert(buffer[0] == 1);
            if (buffer.Length < 4)
            {
                Log.LogServerRequestInvalid(_logger, _sessionId.Value);
                WriteErrorCommandResponse(conversation, 255); // 255 - invalid request
                return;
            }

            KnpServiceType serviceType = (KnpServiceType)buffer[1];
            if (serviceType != KnpServiceType.Tcp && serviceType != KnpServiceType.Udp)
            {
                Log.LogServerRequestUnknownServiceType(_logger, _sessionId.Value);
                WriteErrorCommandResponse(conversation, 2); // 2 - unknown service type
                return;
            }
            if (!KnpParseHelper.TryParseName(buffer.Slice(2), out int bytesRead, out string? name))
            {
                Log.LogServerRequestInvalid(_logger, _sessionId.Value);
                WriteErrorCommandResponse(conversation, 255); // 255 - invalid request
                return;
            }
            bytesRead += 2;

            // bind provider
            IKnpService? service = _server.GetService(name);
            if (service is null || service.ServiceType != serviceType)
            {
                Log.LogServerRequestServiceNotFound(_logger, name, _sessionId.Value);
                WriteErrorCommandResponse(conversation, 3); // 3 - service not found
                return;
            }
            IKnpServiceBinding? serviceBinding = service.CreateBinding(this, _server.AllocateBindingId(), buffer.Slice(bytesRead));
            if (serviceBinding is null)
            {
                Log.LogServerRequestBindingFailed(_logger, name, _sessionId.Value);
                WriteErrorCommandResponse(conversation, 3); // 3 - service not found
                return;
            }
            if (!_bindings.TryAdd(serviceBinding.BindingId, serviceBinding))
            {
                Log.LogServerRequestBindingFailed(_logger, name, _sessionId.Value);
                serviceBinding.Dispose();
                WriteErrorCommandResponse(conversation, 3); // 3 - service not found
                return;
            }
            if (_cts is null)
            {
                // canceled
                if (_bindings.TryRemove(new KeyValuePair<int, IKnpServiceBinding>(serviceBinding.BindingId, serviceBinding)))
                {
                    serviceBinding.Dispose();
                }
                WriteErrorCommandResponse(conversation, 3); // 3 - service not found
                return;
            }

            Log.LogServerBindingCreated(_logger, name, _sessionId.Value);
            WriteCommandResponse(conversation, service, serviceBinding);
        }

        private void ProcessBindVirtualBusCommand(KcpConversation conversation, ReadOnlySpan<byte> buffer)
        {
            Debug.Assert(buffer[0] == 2);
            if (buffer.Length < 3)
            {
                Log.LogServerRequestInvalid(_logger, _sessionId.Value);
                WriteErrorCommandResponse(conversation, 255); // 255 - invalid request
                return;
            }
            if (!KnpParseHelper.TryParseName(buffer.Slice(1), out int bytesRead, out string? name))
            {
                Log.LogServerRequestInvalid(_logger, _sessionId.Value);
                WriteErrorCommandResponse(conversation, 255); // 255 - invalid request
                return;
            }
            bytesRead += 1;

            // bind provider
            KnpVirtualBusService? service = _server.GetVirtualBus(name);
            if (service is null)
            {
                Log.LogServerRequestVirtualBusNotFound(_logger, name, _sessionId.Value);
                WriteErrorCommandResponse(conversation, 3); // 3 - virtual bus not found
                return;
            }
            IKnpServiceBinding? serviceBinding = service.CreateBinding(this, _server.AllocateBindingId(), buffer.Slice(bytesRead));
            if (serviceBinding is null)
            {
                Log.LogServerRequestVirtualBusBindingFailed(_logger, name, _sessionId.Value);
                WriteErrorCommandResponse(conversation, 3); // 3 - failed to create binding
                return;
            }
            if (!_bindings.TryAdd(serviceBinding.BindingId, serviceBinding))
            {
                Log.LogServerRequestVirtualBusBindingFailed(_logger, name, _sessionId.Value);
                serviceBinding.Dispose();
                WriteErrorCommandResponse(conversation, 3); // 3 - failed to create binding
                return;
            }
            if (_cts is null)
            {
                // canceled
                if (_bindings.TryRemove(new KeyValuePair<int, IKnpServiceBinding>(serviceBinding.BindingId, serviceBinding)))
                {
                    serviceBinding.Dispose();
                }
                WriteErrorCommandResponse(conversation, 3); // 3 - virtual bus not found
                return;
            }

            Log.LogServerVirtualBusBindingCreated(_logger, name, _sessionId.Value);
            WriteCommandResponse(conversation, service, serviceBinding);
            return;
        }

        [SkipLocalsInit]
        private static void WriteCommandResponse(KcpConversation channel, IKnpService service, IKnpServiceBinding serviceBinding)
        {
            Span<byte> buffer = stackalloc byte[256];
            buffer[0] = 0;
            buffer[1] = 0;
            BinaryPrimitives.WriteInt32BigEndian(buffer.Slice(2), serviceBinding.BindingId);
            int length = service.WriteParameters(buffer.Slice(6));
            channel.TrySend(buffer.Slice(0, 6 + length));
        }

        [SkipLocalsInit]
        private static void WriteErrorCommandResponse(KcpConversation channel, int errorCode)
        {
            Span<byte> buffer = stackalloc byte[2];
            buffer[0] = 1;
            buffer[1] = (byte)errorCode;
            channel.TrySend(buffer.Slice(0, 2));
        }

        void IKcpNetworkConnectionCallback.NotifyStateChanged(IKcpNetworkConnection connection)
        {
            if (connection.State == KcpNetworkConnectionState.Dead || connection.State == KcpNetworkConnectionState.Failed)
            {
                if (!_unregistered)
                {
                    _unregistered = true;
                    _server.UnregisterClientSession(_sessionId.Value, this);
                }
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

                if (_bindings.TryGetValue(bindingId, out IKnpServiceBinding? serviceBinding))
                {
                    return serviceBinding.InputPacketAsync(packet, cancellationToken);
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

        async void IThreadPoolWorkItem.Execute()
        {
            CancellationTokenSource? cts = _cts;
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

                long tick = Environment.TickCount64;
                foreach (KeyValuePair<long, IKnpForwardSession> item in _forwardSessions)
                {
                    if (item.Value.IsExpired(tick))
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
            if (_cts is null)
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

        ValueTask IKnpConnectionHost.SendAsync(KcpBufferList buffer, CancellationToken cancellationToken)
        {
            if (_cts is null)
            {
                return default;
            }
            return _connection.SendAsync(buffer, cancellationToken);
        }

        bool IKnpConnectionHost.QueuePacket(KcpBufferList buffer)
        {
            if (_cts is null)
            {
                return default;
            }
            return _connection.Send(buffer);
        }
    }
}
