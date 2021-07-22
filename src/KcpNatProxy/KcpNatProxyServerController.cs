using System;
using System.Buffers.Binary;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using KcpSharp;
using Microsoft.Extensions.Logging;

namespace KcpNatProxy
{
    internal sealed class KcpNatProxyServerController : IKcpTransport, IUdpService, IDisposable
    {
        private readonly IUdpServiceDispatcher _sender;
        private readonly EndPoint _endPoint;
        private readonly ListenEndPointTracker _endPointTracker;
        private readonly byte[]? _password;
        private readonly int _mtu;
        private readonly MemoryPool _memoryPool;
        private readonly ILogger _logger;
        private readonly DateTime _createdDateTime;

        private KcpNatProxyServiceWorkerDispatcher? _dispatcher;

        private Guid? _sessionId;
        private CancellationTokenSource? _cts;
        private DateTime _lastHeartbeatDateTime;

        private KcpConversation? _authConversation;
        private KcpConversation? _controlConversation;

        public KcpNatProxyServerController(IUdpServiceDispatcher sender, EndPoint endPoint, ListenEndPointTracker endPointTracker, byte[]? password, int mtu, MemoryPool memoryPool, ILogger logger)
        {
            _sender = sender;
            _endPoint = endPoint;
            _endPointTracker = endPointTracker;
            _password = password;
            _mtu = mtu;
            _memoryPool = memoryPool;
            _logger = logger;
            _createdDateTime = DateTime.UtcNow;

            Log.LogServerNewConnection(_logger, _endPoint);
        }

        public void Dispose()
        {
            Log.LogServerConnectionEliminated(_logger, _endPoint);
        }
        private void Disconnect()
        {
            _sender.RemoveService(_endPoint, this);
        }

        public ValueTask SendPacketAsync(Memory<byte> packet, CancellationToken cancellationToken)
            => _sender.SendPacketAsync(_endPoint, packet, cancellationToken);

        bool IUdpService.ValidateAliveness() => (_sessionId is null && DateTime.UtcNow.AddSeconds(-30) < _createdDateTime) || DateTime.UtcNow.AddSeconds(-90) < _lastHeartbeatDateTime;
        ValueTask IUdpService.InputPacketAsync(ReadOnlyMemory<byte> packet, CancellationToken cancellationToken)
        {
            ReadOnlySpan<byte> span = packet.Span;
            if (!BinaryPrimitives.TryReadInt32LittleEndian(span, out int serviceId))
            {
                return default;
            }
            if (serviceId == 0)
            {
                if (!BinaryPrimitives.TryReadInt32LittleEndian(span.Slice(4), out int channelId))
                {
                    return default;
                }
                if (channelId == 0)
                {
                    KcpConversation? authConversation = _authConversation;
                    if (authConversation is not null)
                    {
                        return authConversation.InputPakcetAsync(packet.Slice(4), cancellationToken);
                    }
                }
                if (channelId == 1)
                {
                    return new ValueTask(ProcessHeartbeart(packet.Slice(8), cancellationToken));
                }
                if (channelId == 2)
                {
                    KcpConversation? controlConversation = _controlConversation;
                    if (controlConversation is not null)
                    {
                        return controlConversation.InputPakcetAsync(packet.Slice(4), cancellationToken);
                    }
                }

                return default;
            }

            KcpNatProxyServiceWorkerDispatcher? dispatcher = _dispatcher;
            if (dispatcher is not null)
            {
                return dispatcher.InputPacketAsync(serviceId, packet.Slice(4), cancellationToken);
            }

            return default;
        }
        void IUdpService.SetTransportClosed()
        {
            try
            {
                Interlocked.Exchange(ref _cts, null)?.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // Ignore
            }
        }

        public void Start()
        {
            _cts = new CancellationTokenSource();
            _ = Task.Run(() => RunAsync(_cts));
        }

        private async Task RunAsync(CancellationTokenSource cts)
        {
            CancellationToken cancellationToken = cts.Token;
            try
            {
                int mtu = _mtu;

                // authenticate
                _authConversation = new KcpConversation(this, 0, new KcpConversationOptions { PreBufferSize = 4, BufferPool = _memoryPool, Mtu = 256 });
                try
                {
                    var context = new KcpNatProxyServerAuthenticationContext(_authConversation, _password, mtu, _logger);
                    if (!await context.AuthenticateAsync(cancellationToken).ConfigureAwait(false))
                    {
                        Disconnect();
                        return;
                    }

                    _lastHeartbeatDateTime = DateTime.UtcNow;
                    _sessionId = Guid.NewGuid();
                    if (!await context.SendSessionIdAsync(_sessionId.GetValueOrDefault(), cancellationToken).ConfigureAwait(false))
                    {
                        Disconnect();
                        return;
                    }

                    mtu = context.NegotiatedMtu;
                    Log.LogServerAuthenticationSuccess(_logger, _endPoint, mtu);
                }
                finally
                {
                    _authConversation.Dispose();
                    _authConversation = null;
                }

                // control
                _controlConversation = new KcpConversation(this, 2, new KcpConversationOptions { PreBufferSize = 4, BufferPool = _memoryPool, Mtu = mtu });
                _dispatcher = new KcpNatProxyServiceWorkerDispatcher(this, _endPointTracker, _controlConversation, _mtu, _memoryPool, _logger);
                try
                {
                    await _dispatcher.RunAsync(cancellationToken).ConfigureAwait(false);
                }
                finally
                {
                    _controlConversation.Dispose();
                    _controlConversation = null;
                }
            }
            catch (OperationCanceledException)
            {
                // Ignore
            }
            catch (ObjectDisposedException)
            {
                // Ignore
            }
            finally
            {
                Disconnect();
                cts.Dispose();
            }
        }

        private async Task ProcessHeartbeart(ReadOnlyMemory<byte> content, CancellationToken cancellationToken)
        {
            if (content.Length < 16)
            {
                return;
            }
            var receivedGuid = new Guid(content.Span.Slice(0, 16));
            using KcpRentedBuffer handle = _memoryPool.Rent(new KcpBufferPoolRentOptions(24, true));
            Memory<byte> memory = handle.Memory;
            BinaryPrimitives.WriteInt32LittleEndian(memory.Span, 0);
            BinaryPrimitives.WriteInt32LittleEndian(memory.Span.Slice(4), 1);
            bool shouldDisconnect = false;
            if (_sessionId.HasValue)
            {
                if (receivedGuid == _sessionId.GetValueOrDefault())
                {
                    _lastHeartbeatDateTime = DateTime.UtcNow;
                    receivedGuid.TryWriteBytes(memory.Span.Slice(8, 16));
                }
                else
                {
                    memory.Span.Slice(8, 16).Clear();
                    shouldDisconnect = true;
                }
            }
            else
            {
                memory.Span.Slice(8, 16).Clear();
            }
            await SocketHelper.SendToAndLogErrorAsync(_sender, _endPoint, _logger, memory.Slice(0, 24), cancellationToken).ConfigureAwait(false);
            if (shouldDisconnect)
            {
                Disconnect();
            }
        }
    }
}
