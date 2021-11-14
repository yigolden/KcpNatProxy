using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.Logging;

namespace KcpNatProxy.Server
{
    internal sealed class KnpVirtualBusService : IKnpService
    {
        private readonly string _name;
        private readonly KnpVirtualBusRelayType _relayType;
        private readonly ILogger _logger;
        private readonly KnpVirtualBusServiceProviderCollection _providerCollection;

        private bool _disposed;
        private readonly ConcurrentDictionary<int, KnpVirtualBusServiceBinding> _clients = new();

        public string ServiceName => _name;
        KnpServiceType IKnpService.ServiceType => KnpServiceType.VirtualBus;
        public KnpVirtualBusRelayType RelayType => _relayType;

        public KnpVirtualBusService(string name, KnpVirtualBusRelayType relayType, ILogger logger)
        {
            _name = name;
            _relayType = relayType;
            _logger = logger;
            _providerCollection = new KnpVirtualBusServiceProviderCollection(new KnpInt32IdAllocator());
        }

        public int WriteParameters(Span<byte> buffer)
        {
            buffer[0] = (byte)_relayType;
            return 1;
        }

        public void Start()
        {
            if (_disposed)
            {
                return;
            }
            Log.LogCommonVirtualBusServiceStarted(_logger, _name, _relayType);
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            KnpCollectionHelper.ClearAndDispose(_clients);
            _providerCollection.Dispose();
        }

        public KnpVirtualBusServiceBinding? FindClient(int sessionId)
        {
            if (_disposed)
            {
                return null;
            }
            if (_clients.TryGetValue(sessionId, out KnpVirtualBusServiceBinding? serviceBinding))
            {
                return serviceBinding;
            }
            return null;
        }

        public IKnpServiceBinding? CreateBinding(IKnpConnectionHost host, KnpRentedInt32 bindingId, ReadOnlySpan<byte> parameters)
        {
            if (_disposed)
            {
                return null;
            }
            if (!host.TryGetSessionId(out int sessionId, out _))
            {
                return null;
            }

            KnpVirtualBusServiceBinding binding = _clients.GetOrAdd(sessionId, static (sessionId, state) => new KnpVirtualBusServiceBinding(state.Service, state.Host, state.BindingId), (Service: this, Host: host, BindingId: bindingId));
            if (binding.BindingId != bindingId.Value)
            {
                binding.Dispose();
                return null;
            }
            if (_disposed)
            {
                binding.Dispose();
                return null;
            }
            binding.Start();
            return binding;
        }

        internal void UnregisterBinding(KnpVirtualBusServiceBinding serviceBinding)
        {
            _ = _clients.TryRemove(new KeyValuePair<int, KnpVirtualBusServiceBinding>(serviceBinding.SessionId, serviceBinding));
            _providerCollection.RemoveForServiceBinding(serviceBinding.SessionId);
        }

        internal bool TryGetSessionForForward(int sessionId, [NotNullWhen(true)] out KnpVirtualBusServiceBinding? session)
        {
            if (_relayType == KnpVirtualBusRelayType.Always || _relayType == KnpVirtualBusRelayType.Mixed)
            {
                return _clients.TryGetValue(sessionId, out session);
            }
            session = null;
            return false;
        }

        internal KnpVirtualBusProviderInfo? TryAddProvider(int sessionId, string name, KnpServiceType serviceType, ReadOnlySpan<byte> parameters)
        {
            KnpVirtualBusProviderInfo? providerInfo = _providerCollection.TryAddProvider(sessionId, name, serviceType, parameters);
            if (providerInfo is not null)
            {
                Log.LogServerVirtualBusProviderAdded(_logger, _name, name, serviceType, sessionId);
            }
            return providerInfo;
        }

        internal void RemoveProvider(KnpVirtualBusProviderInfo provider)
        {
            Log.LogServerVirtualBusProviderRemoved(_logger, _name, provider.Name, provider.ServiceType, provider.SessionId);
            _providerCollection.RemoveProvider(provider);
        }

        internal KnpVirtualBusProviderInfo? QueryProvider(string name, KnpServiceType serviceType)
            => _providerCollection.QueryProvider(name, serviceType);
    }
}
