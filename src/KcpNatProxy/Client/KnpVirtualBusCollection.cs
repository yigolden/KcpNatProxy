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
    internal sealed class KnpVirtualBusCollection : KnpProviderFactory, IAsyncDisposable
    {
        private readonly int _maximumMtu;
        private readonly KnpPeerConnectionReceiver _peerConnectionReceiver;
        private readonly IKcpBufferPool _bufferPool;
        private readonly ILogger _logger;
        private readonly Dictionary<string, KnpVirtualBusLifetime> _buses = new(StringComparer.Ordinal);

        public bool IsEmpty => _buses.Count == 0;

        public KnpVirtualBusCollection(int maximumMtu, KnpPeerConnectionReceiver peerConnectionReceiver, IKcpBufferPool bufferPool, ILogger logger)
        {
            _maximumMtu = maximumMtu;
            _peerConnectionReceiver = peerConnectionReceiver;
            _bufferPool = bufferPool;
            _logger = logger;
        }

        public async ValueTask DisposeAsync()
        {
            foreach (KeyValuePair<string, KnpVirtualBusLifetime> item in _buses)
            {
                await item.Value.DisposeAsync().ConfigureAwait(false);
            }
        }

        public KnpVirtualBusLifetime GetOrCreate(string virtualBusName)
        {
            if (_buses.TryGetValue(virtualBusName, out KnpVirtualBusLifetime? lifetime))
            {
                return lifetime;
            }
            lifetime = new KnpVirtualBusLifetime(_peerConnectionReceiver, virtualBusName, _maximumMtu, _bufferPool, _logger);
            _buses.Add(virtualBusName, lifetime);
            return lifetime;
        }

        public override async Task<bool> RegisterAsync(KcpConversation channel, IKnpConnectionHost consumer, ConcurrentDictionary<int, IKnpProvider> providers, CancellationToken cancellationToken)
        {
            foreach (KeyValuePair<string, KnpVirtualBusLifetime> item in _buses)
            {
                await channel.WaitForSendQueueAvailableSpaceAsync(0, 1, cancellationToken).ConfigureAwait(false);
                SendRequest(channel, item.Key);
                KcpConversationReceiveResult result = await channel.WaitToReceiveAsync(cancellationToken).ConfigureAwait(false);
                KnpVirtualBusProvider? provider = ReceiveResponse(channel, result, item.Value, consumer, providers);
                if (provider is null)
                {
                    return false;
                }
                await provider.RegisterVirtualBusProvidersAsync(cancellationToken).ConfigureAwait(false);
            }
            return true;
        }

        [SkipLocalsInit]
        private void SendRequest(KcpConversation channel, string name)
        {
            Span<byte> buffer = stackalloc byte[256];
            buffer[0] = 2; // type: 2-bind virtual bus
            buffer[1] = (byte)Encoding.UTF8.GetByteCount(name);
            int bytesWritten = 2 + Encoding.UTF8.GetBytes(name, buffer.Slice(2));
            _ = channel.TrySend(buffer.Slice(0, bytesWritten));
        }

        [SkipLocalsInit]
        private KnpVirtualBusProvider? ReceiveResponse(KcpConversation channel, KcpConversationReceiveResult result, KnpVirtualBusLifetime virtualBus, IKnpConnectionHost consumer, ConcurrentDictionary<int, IKnpProvider> providers)
        {
            Span<byte> buffer = stackalloc byte[8];
            bool succeeded = channel.TryReceive(buffer, out result);
            if (!succeeded)
            {
                return null;
            }

            if (result.BytesReceived == 2)
            {
                if (buffer[0] == 1)
                {
                    int reason = buffer[1];
                    if (reason == 3)
                    {
                        Log.LogClientBindProviderServiceNotFound(_logger, virtualBus.Name);
                        return null;
                    }
                }
            }

            if (result.BytesReceived < 7)
            {
                Log.LogClientBindServiceBusInvalidResponse(_logger, virtualBus.Name);
                return null;
            }

            KnpVirtualBusProvider? provider = TryParseBindProviderResponse(consumer, virtualBus, buffer, out int bindingId);
            if (provider is null)
            {
                Log.LogClientBindServiceBusInvalidResponse(_logger, virtualBus.Name);
                return null;
            }

            if (!providers.TryAdd(bindingId, provider))
            {
                Log.LogClientBindServiceBusInvalidResponse(_logger, virtualBus.Name);
                provider.Dispose();
                return null;
            }

            Log.LogClientBindServiceBusSucceeded(_logger, virtualBus.Name);
            return provider;
        }

        public static KnpVirtualBusProvider? TryParseBindProviderResponse(IKnpConnectionHost host, KnpVirtualBusLifetime virtualBus, ReadOnlySpan<byte> buffer, out int bindingId)
        {
            Debug.Assert(buffer.Length >= 7);
            if (buffer[0] != 0)
            {
                bindingId = 0;
                return null;
            }
            bindingId = BinaryPrimitives.ReadInt32BigEndian(buffer.Slice(2));
            if (bindingId < 0)
            {
                return null;
            }

            return virtualBus.GetProviderRegistration(host, bindingId, (KnpVirtualBusRelayType)buffer[6]);
        }
    }
}
