using System;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using KcpSharp;
using Microsoft.Extensions.Logging;

namespace KcpNatProxy
{
    internal sealed class KcpNatProxyServiceWorkerDispatcher
    {
        private readonly KcpNatProxyServerController _controller;
        private readonly ListenEndPointTracker _tracker;
        private readonly KcpConversation _controlConversation;
        private readonly int _mtu;
        private readonly MemoryPool _memoryPool;
        private readonly ILogger _logger;

        private bool _isRunning;
        private readonly ServiceNameTracker _serviceNamesTracker;
        private readonly ConcurrentDictionary<int, KcpNatProxyServiceWorker> _serviceWorkers;
        private readonly Int32IdPool _serviceIdPool;

        public ListenEndPointTracker EndPointTracker => _tracker;
        public Int32IdPool ServiceIdPool => _serviceIdPool;

        public KcpNatProxyServiceWorkerDispatcher(KcpNatProxyServerController controller, ListenEndPointTracker tracker, KcpConversation controlConversation, int mtu, MemoryPool memoryPool, ILogger logger)
        {
            _controller = controller;
            _tracker = tracker;
            _controlConversation = controlConversation;
            _mtu = mtu;
            _memoryPool = memoryPool;
            _logger = logger;

            _serviceNamesTracker = new ServiceNameTracker();
            _serviceWorkers = new ConcurrentDictionary<int, KcpNatProxyServiceWorker>();
            _serviceIdPool = new Int32IdPool();
        }

        public async Task RunAsync(CancellationToken cancellationToken)
        {
            _isRunning = true;
            try
            {
                await ProcessControlChannelLoop(cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _isRunning = false;
                CleanUpWorkers();
                _serviceIdPool.Clear();
            }
        }

        private void CleanUpWorkers()
        {
            foreach (KeyValuePair<int, KcpNatProxyServiceWorker> item in _serviceWorkers)
            {
                if (_serviceWorkers.TryRemove(item))
                {
                    KcpNatProxyServiceType type = item.Value.Type;
                    string name = item.Value.Name;
                    item.Value.Dispose();
                    switch (type)
                    {
                        case KcpNatProxyServiceType.Tcp:
                            Log.LogServerTcpServiceDestroyed(_logger, name);
                            break;
                        case KcpNatProxyServiceType.Udp:
                            Log.LogServerUdpServiceDestroyed(_logger, name);
                            break;
                    }
                }
            }
        }

        public ValueTask SendPacketAsync(Memory<byte> packet, CancellationToken cancellationToken)
            => _controller.SendPacketAsync(packet, cancellationToken);


        public ValueTask InputPacketAsync(int serviceId, ReadOnlyMemory<byte> packet, CancellationToken cancellationToken)
        {
            if (!_isRunning)
            {
                return default;
            }
            if (_serviceWorkers.TryGetValue(serviceId, out KcpNatProxyServiceWorker? worker))
            {
                return worker.InputPacketAsync(packet, cancellationToken);
            }
            return default;
        }

        private async Task ProcessControlChannelLoop(CancellationToken cancellationToken)
        {
            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    const int MaximumMessageSize = 1024;

                    KcpConversationReceiveResult result = await _controlConversation.WaitToReceiveAsync(cancellationToken).ConfigureAwait(false);
                    if (result.TransportClosed || result.BytesReceived > MaximumMessageSize)
                    {
                        await SendInvalidCommand(cancellationToken).ConfigureAwait(false);
                        continue;
                    }

                    await ParseAndProcesMessage(result, cancellationToken).ConfigureAwait(false);
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
            catch (Exception ex)
            {
                Log.LogUnhandledExceptionInControlChannelProcessing(_logger, ex);
            }
        }

        [SkipLocalsInit]
        private ValueTask ParseAndProcesMessage(KcpConversationReceiveResult result, CancellationToken cancellationToken)
        {
            Span<byte> buffer = stackalloc byte[result.BytesReceived];
            if (!_controlConversation.TryReceive(buffer, out result))
            {
                return default;
            }

            if (buffer[0] == 1)
            {
                // create service
                return ProcessCreateServiceCommand(buffer.Slice(1, result.BytesReceived - 1), cancellationToken);
            }
            else
            {
                return new ValueTask(SendInvalidCommand(cancellationToken));
            }
        }

        private async Task SendInvalidCommand(CancellationToken cancellationToken)
        {
            if (await _controlConversation.WaitForSendQueueAvailableSpaceAsync(0, 1, cancellationToken).ConfigureAwait(false))
            {
                ushort b = 0xffff;
                _controlConversation.TrySend(MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref b, 1)));
            }
        }

        private ValueTask ProcessCreateServiceCommand(ReadOnlySpan<byte> content, CancellationToken cancellationToken)
        {
            if (content.IsEmpty)
            {
                return new ValueTask(SendInvalidCommand(cancellationToken));
            }

            // valiedate name
            int nameLength = content[0];
            content = content.Slice(1);
            if (nameLength > 64 || content.Length < nameLength)
            {
                return new ValueTask(SendInvalidCommand(cancellationToken));
            }
            ReadOnlySpan<byte> nameSpan = content.Slice(0, nameLength);
            content = content.Slice(nameLength);
            foreach (char c in nameSpan)
            {
                if (c < 0x20 || c > 0x7e)
                {
                    return new ValueTask(SendInvalidCommand(cancellationToken));
                }
            }
            string name = Encoding.ASCII.GetString(nameSpan);
            ServiceNameHandle? nameHandle = _serviceNamesTracker.Track(name);
            if (nameHandle is null)
            {
                return new ValueTask(SendCreateCommandNameUnavailable(cancellationToken));
            }
            try
            {
                if (content.Length < 2)
                {
                    return new ValueTask(SendInvalidCommand(cancellationToken));
                }
                // create service
                var type = (KcpNatProxyServiceType)content[0];
                byte endpointType = content[1];
                content = content.Slice(2);
                IPEndPoint? ipep;
                if (endpointType == 1)
                {
                    // ipv4
                    if (content.Length < 6)
                    {
                        return new ValueTask(SendInvalidCommand(cancellationToken));
                    }
                    int port = BinaryPrimitives.ReadUInt16LittleEndian(content.Slice(4, 2));
                    if (port == 0)
                    {
                        return new ValueTask(SendInvalidCommand(cancellationToken));
                    }
                    ipep = new IPEndPoint(new IPAddress(content.Slice(0, 4)), port);
                    content = content.Slice(6);
                }
                else if (endpointType == 2)
                {
                    // ipv6
                    if (content.Length < 18)
                    {
                        return new ValueTask(SendInvalidCommand(cancellationToken));
                    }
                    int port = BinaryPrimitives.ReadUInt16LittleEndian(content.Slice(16, 2));
                    if (port == 0)
                    {
                        return new ValueTask(SendInvalidCommand(cancellationToken));
                    }
                    ipep = new IPEndPoint(new IPAddress(content.Slice(0, 16)), port);
                    content = content.Slice(18);
                }
                else
                {
                    return new ValueTask(SendInvalidCommand(cancellationToken));
                }

                bool noDelay = false;
                if (!content.IsEmpty)
                {
                    // parse options
                    byte optionLength = content[0];
                    content = content.Slice(1);
                    if (content.Length < optionLength)
                    {
                        return new ValueTask(SendInvalidCommand(cancellationToken));
                    }

                    ReadOnlySpan<byte> optionsSpan = content.Slice(0, optionLength);
                    foreach (byte c in optionsSpan)
                    {
                        if (c == 1)
                        {
                            noDelay = true;
                        }
                    }
                }

                return new ValueTask(HandleCreateService(Interlocked.Exchange<ServiceNameHandle?>(ref nameHandle, null), type, ipep, noDelay, cancellationToken));
            }
            finally
            {
                nameHandle?.Dispose();
            }
        }

        private async Task SendCreateCommandNameUnavailable(CancellationToken cancellationToken)
        {
            if (await _controlConversation.WaitForSendQueueAvailableSpaceAsync(0, 1, cancellationToken).ConfigureAwait(false))
            {
                SendCore(_controlConversation);
            }

            static void SendCore(KcpConversation conversation)
            {
                Span<byte> buffer = stackalloc byte[2];
                buffer[0] = 1;
                buffer[1] = 1;
                conversation.TrySend(buffer);
            }
        }

        private async Task SendCreateEndPointUnavailable(CancellationToken cancellationToken)
        {
            if (await _controlConversation.WaitForSendQueueAvailableSpaceAsync(0, 1, cancellationToken).ConfigureAwait(false))
            {
                SendCore(_controlConversation);
            }

            static void SendCore(KcpConversation conversation)
            {
                Span<byte> buffer = stackalloc byte[2];
                buffer[0] = 1;
                buffer[1] = 2;
                conversation.TrySend(buffer);
            }
        }

        private async Task SendCreateUnspecifiedError(CancellationToken cancellationToken)
        {
            if (await _controlConversation.WaitForSendQueueAvailableSpaceAsync(0, 1, cancellationToken).ConfigureAwait(false))
            {
                SendCore(_controlConversation);
            }

            static void SendCore(KcpConversation conversation)
            {
                Span<byte> buffer = stackalloc byte[2];
                buffer[0] = 1;
                buffer[1] = 255;
                conversation.TrySend(buffer);
            }
        }

        private async Task SendCreateSucceed(int serviceId, CancellationToken cancellationToken)
        {
            if (await _controlConversation.WaitForSendQueueAvailableSpaceAsync(0, 1, cancellationToken).ConfigureAwait(false))
            {
                SendCore(serviceId, _controlConversation);
            }

            static void SendCore(int serviceId, KcpConversation conversation)
            {
                Span<byte> buffer = stackalloc byte[6];
                buffer[0] = 0;
                buffer[1] = 0;
                BinaryPrimitives.WriteInt32LittleEndian(buffer.Slice(2), serviceId);
                conversation.TrySend(buffer);
            }
        }


        private async Task HandleCreateService(ServiceNameHandle nameHandle, KcpNatProxyServiceType type, IPEndPoint listenEndPoint, bool noDelay, CancellationToken cancellationToken)
        {
            if (type == KcpNatProxyServiceType.Tcp)
            {
                KcpNatProxyServerTcpListener? listener = KcpNatProxyServerTcpListener.Create(this, nameHandle, listenEndPoint,noDelay, _mtu, _memoryPool, _logger);
                if (listener is null)
                {
                    nameHandle.Dispose();
                    await SendCreateEndPointUnavailable(cancellationToken).ConfigureAwait(false);
                    return;
                }
                if (!_serviceWorkers.TryAdd(listener.Id, listener))
                {
                    nameHandle.Dispose();
                    listener.Dispose();
                    await SendCreateUnspecifiedError(cancellationToken).ConfigureAwait(false);
                    return;
                }
                listener.Start();
                Log.LogServerTcpServiceCreated(_logger, nameHandle.Name, listenEndPoint);
                await SendCreateSucceed(listener.Id, cancellationToken).ConfigureAwait(false);
            }
            else if (type == KcpNatProxyServiceType.Udp)
            {
                KcpNatProxyServerUdpListener? listener = KcpNatProxyServerUdpListener.Create(this, nameHandle, listenEndPoint, _mtu, _memoryPool);
                if (listener is null)
                {
                    nameHandle.Dispose();
                    await SendCreateEndPointUnavailable(cancellationToken).ConfigureAwait(false);
                    return;
                }
                if (!_serviceWorkers.TryAdd(listener.Id, listener))
                {
                    nameHandle.Dispose();
                    listener.Dispose();
                    await SendCreateUnspecifiedError(cancellationToken).ConfigureAwait(false);
                    return;
                }
                listener.Start();
                Log.LogServerUdpServiceCreated(_logger, nameHandle.Name, listenEndPoint);
                await SendCreateSucceed(listener.Id, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                nameHandle.Dispose();
                await SendInvalidCommand(cancellationToken).ConfigureAwait(false);
            }
        }

    }
}
