using System;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using KcpSharp;
using Microsoft.Extensions.Logging;

namespace KcpNatProxy.Client
{
    internal readonly struct KnpVirtualBusControlChannelProviderRegistration
    {
        private readonly string _virtualBusName;
        private readonly KcpConversation _conversation;
        private readonly KnpVirtualBusControlChannelRequestList _sender;
        private readonly ILogger _logger;

        public KnpVirtualBusControlChannelProviderRegistration(string virtualBusName, KcpConversation conversation, KnpVirtualBusControlChannelRequestList sender, ILogger logger)
        {
            _virtualBusName = virtualBusName;
            _conversation = conversation;
            _sender = sender;
            _logger = logger;
        }

        public async Task RegisterProvidersAsync(IEnumerable<IKnpVirtualBusProviderFactory> providerFactories, ConcurrentDictionary<int, IKnpVirtualBusProviderFactory> factoryMappings, CancellationToken cancellationToken)
        {
            if (_conversation is null || _sender is null || _logger is null)
            {
                return;
            }

            foreach (IKnpVirtualBusProviderFactory item in providerFactories)
            {
                await _conversation.WaitForSendQueueAvailableSpaceAsync(0, 1, cancellationToken).ConfigureAwait(false);
                SendRequest(_conversation, item);
                KcpConversationReceiveResult result = await _conversation.WaitToReceiveAsync(cancellationToken).ConfigureAwait(false);
                if (result.BytesReceived == 0)
                {
                    break;
                }
                int? bindingId = ProcessResponse(_conversation, result);
                if (!bindingId.HasValue)
                {
                    break;
                }
                if (factoryMappings.TryAdd(bindingId.GetValueOrDefault(), item))
                {
                    Log.LogClientVirtualBusProviderAdded(_logger, _virtualBusName, item.Name, item.ServiceType);
                }
            }

            _sender.Start();
        }

        [SkipLocalsInit]
        private static void SendRequest(KcpConversation conversation, IKnpVirtualBusProviderFactory factory)
        {
            Span<byte> buffer = stackalloc byte[256];
            buffer[0] = 1;
            buffer[1] = (byte)factory.ServiceType;
            int bytesWritten = Encoding.UTF8.GetBytes(factory.Name, buffer.Slice(3));
            Debug.Assert(bytesWritten <= 128);
            buffer[2] = (byte)bytesWritten;
            bytesWritten += 3;
            bytesWritten += factory.SerializeParameters(buffer.Slice(bytesWritten));
            conversation.TrySend(buffer.Slice(0, bytesWritten));
        }

        private static int? ProcessResponse(KcpConversation conversation, KcpConversationReceiveResult result)
        {
            Span<byte> buffer = stackalloc byte[6];
            if (!conversation.TryReceive(buffer, out result) || result.BytesReceived < 2)
            {
                return null;
            }
            if (buffer[0] != 0)
            {
                // failure
                // TODO check and log reason
                return 0;
            }
            if (result.BytesReceived < 6)
            {
                // invalid packet
                return null;
            }
            return BinaryPrimitives.ReadInt32BigEndian(buffer.Slice(2));
        }
    }
}
