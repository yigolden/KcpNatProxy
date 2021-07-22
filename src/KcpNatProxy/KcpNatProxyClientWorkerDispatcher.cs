using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using KcpSharp;
using Microsoft.Extensions.Logging;

namespace KcpNatProxy
{
    internal sealed class KcpNatProxyClientWorkerDispatcher
    {
        private readonly KcpNatProxyClientController _controller;
        private readonly int _mtu;
        private readonly MemoryPool _memoryPool;
        private readonly ILogger _logger;

        private readonly Dictionary<int, KcpNatProxyClientWorker> _workers;
        private bool _isRunning;

        public KcpNatProxyClientWorkerDispatcher(KcpNatProxyClientController controller, int mtu, MemoryPool memoryPool, ILogger logger)
        {
            _controller = controller;
            _mtu = mtu;
            _memoryPool = memoryPool;
            _logger = logger;

            _workers = new Dictionary<int, KcpNatProxyClientWorker>();
        }

        public async Task RunAsync(KcpConversation conversation, IReadOnlyList<KcpNatProxyServiceDescriptor> services, CancellationToken cancellationToken)
        {
            try
            {
                foreach (KcpNatProxyServiceDescriptor service in services)
                {
                    if (!SendCreateServiceRequest(conversation, service))
                    {
                        return;
                    }

                    KcpConversationReceiveResult result = await conversation.WaitToReceiveAsync(cancellationToken).ConfigureAwait(false);
                    if (result.TransportClosed)
                    {
                        return;
                    }

                    await ProcessControlRequest(conversation, service, result, cancellationToken).ConfigureAwait(false);

                    await conversation.FlushAsync(cancellationToken).ConfigureAwait(false);
                }
            }
            finally
            {
                await conversation.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            _isRunning = true;
            try
            {
                foreach (KcpNatProxyClientWorker worker in _workers.Values)
                {
                    worker.Start();
                }

                var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                using (cancellationToken.UnsafeRegister(s => ((TaskCompletionSource?)s)!.TrySetResult(), tcs))
                {
                    await tcs.Task.ConfigureAwait(false);
                }
            }
            finally
            {
                foreach (KcpNatProxyClientWorker worker in _workers.Values)
                {
                    worker.Dispose();
                }
                _isRunning = false;
            }
        }

        [SkipLocalsInit]
        private static bool SendCreateServiceRequest(KcpConversation conversation, KcpNatProxyServiceDescriptor service)
        {
            string name = service.Name;

            Span<byte> buffer = stackalloc byte[128];
            buffer[0] = 1;
            int bytesWritten = Encoding.ASCII.GetBytes(name, buffer.Slice(2));
            buffer[1] = (byte)bytesWritten;

            IPAddress address = service.RemoteListen.Address;

            bytesWritten = 2 + bytesWritten;
            Span<byte> endpointBuffer = buffer.Slice(bytesWritten);
            endpointBuffer[0] = (byte)service.Type;
            if (address.AddressFamily == AddressFamily.InterNetworkV6)
            {
                endpointBuffer[1] = 2;
                address.TryWriteBytes(endpointBuffer.Slice(2, 16), out _);
                endpointBuffer = endpointBuffer.Slice(18);
                bytesWritten += 18;
            }
            else if (address.AddressFamily == AddressFamily.InterNetwork)
            {
                endpointBuffer[1] = 1;
                address.TryWriteBytes(endpointBuffer.Slice(2, 4), out _);
                endpointBuffer = endpointBuffer.Slice(6);
                bytesWritten += 6;
            }
            else
            {
                return false;
            }
            BinaryPrimitives.WriteUInt16LittleEndian(endpointBuffer, (ushort)service.RemoteListen.Port);
            bytesWritten += 2;
            if (service.Type == KcpNatProxyServiceType.Tcp && service.NoDelay)
            {
                buffer[bytesWritten++] = 1; // option length
                buffer[bytesWritten++] = 1; // no-delay option
            }

            return conversation.TrySend(buffer.Slice(0, bytesWritten));
        }

        private ValueTask ProcessControlRequest(KcpConversation conversation, KcpNatProxyServiceDescriptor service, KcpConversationReceiveResult result, CancellationToken cancellationToken)
        {
            if (result.BytesReceived < 2 || result.BytesReceived > 6)
            {
                Log.LogClientCreateServiceResponseInvalidPacket(_logger, service.Name);
                return default;
            }

            Span<byte> buffer = stackalloc byte[6];
            if (!conversation.TryReceive(buffer, out result) || result.BytesReceived < 2)
            {
                return default;
            }

            byte status = buffer[0];
            byte errorType = buffer[1];

            if (status == 255)
            {
                Log.LogClientCreateServiceResponseInvalidCommand(_logger, service.Name);
                return default;
            }
            if (status != 0)
            {
                switch (errorType)
                {
                    case 1:
                        Log.LogClientCreateServiceResponseNameUnavailable(_logger, service.Name);
                        break;
                    case 2:
                        Log.LogClientCreateServiceResponseEndPointUnavailable(_logger, service.Name);
                        break;
                    default:
                        Log.LogClientCreateServiceResponseUnspecified(_logger, service.Name);
                        break;
                }
                return default;
            }

            if (result.BytesReceived != 6)
            {
                Log.LogClientCreateServiceResponseInvalidPacket(_logger, service.Name);
                return default;
            }

            int serviceId = BinaryPrimitives.ReadInt32LittleEndian(buffer.Slice(2, 4));
            Log.LogClientCreateServiceSucceeded(_logger, service.Name, serviceId);
            return CreateServiceAsync(serviceId, service, cancellationToken);
        }

        private ValueTask CreateServiceAsync(int serviceId, KcpNatProxyServiceDescriptor service, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            KcpNatProxyClientWorker worker;
            switch (service.Type)
            {
                case KcpNatProxyServiceType.Tcp:
                    worker = new KcpNatProxyClientTcpWorker(this, serviceId, service.LocalForward, service.NoDelay, _mtu, _memoryPool, _logger);
                    break;
                case KcpNatProxyServiceType.Udp:
                    worker = new KcpNatProxyClientUdpWorker(this, serviceId, service.LocalForward, _mtu, _memoryPool, _logger);
                    break;
                default:
                    return default;
            }
            if (!_workers.TryAdd(serviceId, worker))
            {
                Log.LogClientCreateServiceResponseServiceIdInvalid(_logger, service.Name);
                worker.Dispose();
            }

            return default;
        }

        public ValueTask SendPacketAsync(Memory<byte> packet, CancellationToken cancellationToken)
            => _controller.SendPacketAsync(packet, cancellationToken);

        public ValueTask InputPacketAsync(int serviceId, ReadOnlyMemory<byte> packet, CancellationToken cancellationToken)
        {
            if (!_isRunning)
            {
                return default;
            }
            if (_workers.TryGetValue(serviceId, out KcpNatProxyClientWorker? worker))
            {
                return worker.InputPacketAsync(packet, cancellationToken);
            }
            return default;
        }


    }
}
