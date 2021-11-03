using System;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using KcpSharp;

namespace KcpNatProxy.Client
{
    internal sealed class KnpVirtualBusControlChannel : IKnpForwardSession, IKcpTransport
    {
        private readonly string _virtualBusName;
        private IKnpConnectionHost? _host;

        private readonly byte[] _bindingSessionIdBytes;

        private KcpConversation? _conversation;
        private KnpVirtualBusControlChannelRequestList? _sender;

        public KnpVirtualBusControlChannel(string virtualBusName, IKnpConnectionHost host, int bindingId)
        {
            _virtualBusName = virtualBusName;
            _host = host;

            _bindingSessionIdBytes = new byte[8];
            BinaryPrimitives.WriteInt32BigEndian(_bindingSessionIdBytes, bindingId);
            host.TryRegister(MemoryMarshal.Read<long>(_bindingSessionIdBytes), this);

            _conversation = new KcpConversation(this, new KcpConversationOptions { Mtu = host.Mtu, PreBufferSize = 8, BufferPool = host.BufferPool });
            _sender = new KnpVirtualBusControlChannelRequestList(_conversation, _host.Logger);
        }

        public bool IsExpired(DateTime utcNow) => false;
        public void Dispose()
        {
            Interlocked.Exchange(ref _conversation, null)?.Dispose();
            Interlocked.Exchange(ref _sender, null)?.Dispose();
            Interlocked.Exchange(ref _host, null)?.TryUnregister(MemoryMarshal.Read<long>(_bindingSessionIdBytes), this);
        }

        ValueTask IKnpForwardSession.InputPacketAsync(ReadOnlyMemory<byte> packet, CancellationToken cancellationToken)
        {
            KcpConversation? conversation = _conversation;
            if (conversation is null)
            {
                return default;
            }
            return conversation.InputPakcetAsync(packet, cancellationToken);
        }

        ValueTask IKcpTransport.SendPacketAsync(Memory<byte> packet, CancellationToken cancellationToken)
        {
            Debug.Assert(packet.Length >= 8);
            IKnpConnectionHost? host = _host;
            if (host is null)
            {
                return default;
            }

            _bindingSessionIdBytes.CopyTo(packet);

            using var bufferList = KcpRentedBufferList.Allocate(packet);
            return host.SendAsync(bufferList, cancellationToken);
        }

        public ValueTask<KnpVirtualBusProviderSessionBinding> QueryAsync(IKnpService service, CancellationToken cancellationToken)
        {
            Debug.Assert(service.ServiceType == KnpServiceType.Tcp || service.ServiceType == KnpServiceType.Udp);
            Debug.Assert(Encoding.UTF8.GetByteCount(service.ServiceName) <= 128);

            KnpVirtualBusControlChannelRequestList? sender = _sender;
            if (sender is null)
            {
                return default;
            }

            return sender.SendAsync<KnpVirtualBusControlChannelQueryProviderParameter, KnpVirtualBusProviderSessionBinding>(new KnpVirtualBusControlChannelQueryProviderParameter(service), cancellationToken);
        }

        public ValueTask<KnpVirtualBusPeerConnectionInfo> ConnectPeerAsync(int sessionId, CancellationToken cancellationToken)
        {
            KnpVirtualBusControlChannelRequestList? sender = _sender;
            if (sender is null)
            {
                return default;
            }

            return sender.SendAsync<KnpVirtualBusControlChannelConnectPeerParameter, KnpVirtualBusPeerConnectionInfo>(new KnpVirtualBusControlChannelConnectPeerParameter(sessionId), cancellationToken);
        }

        public Task RegisterProvidersAsync(IEnumerable<IKnpVirtualBusProviderFactory> providerFactories, ConcurrentDictionary<int, IKnpVirtualBusProviderFactory> factoryMappings, CancellationToken cancellationToken)
        {
            KcpConversation? conversation = _conversation;
            IKnpConnectionHost? host = _host;
            KnpVirtualBusControlChannelRequestList? sender = _sender;
            if (conversation is null || sender is null || host is null)
            {
                return Task.CompletedTask;
            }
            var registration = new KnpVirtualBusControlChannelProviderRegistration(_virtualBusName, conversation, sender, host.Logger);
            return registration.RegisterProvidersAsync(providerFactories, factoryMappings, cancellationToken);
        }
    }
}
