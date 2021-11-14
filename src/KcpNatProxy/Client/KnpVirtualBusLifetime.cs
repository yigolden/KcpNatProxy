using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using KcpSharp;
using Microsoft.Extensions.Logging;

namespace KcpNatProxy.Client
{
    internal sealed class KnpVirtualBusLifetime : IAsyncDisposable
    {
        private readonly KnpPeerConnectionReceiver _peerConnectionReceiver;
        private readonly ILogger _logger;
        private readonly KnpPeerConnectionCollection _peerConnectionCollection;
        private List<IKnpVirtualBusProviderFactory> _providerFactories = new();
        private List<IKnpService> _services = new();

        private KnpVirtualBusProvider? _currentProvider;

        public string Name { get; }

        public KnpVirtualBusLifetime(KnpPeerConnectionReceiver peerConnectionReceiver, string name, int maximumMtu, IKcpBufferPool bufferPool, ILogger logger)
        {
            _peerConnectionReceiver = peerConnectionReceiver;
            Name = name;
            _logger = logger;
            _peerConnectionCollection = new(peerConnectionReceiver, maximumMtu, bufferPool, logger);
        }

        public ValueTask DisposeAsync() => _peerConnectionCollection.DisposeAsync();

        public void AddProvider(IKnpVirtualBusProviderFactory factory)
        {
            foreach (IKnpVirtualBusProviderFactory item in _providerFactories)
            {
                if (string.Equals(item.Name, factory.Name, StringComparison.Ordinal) || item.ServiceType == factory.ServiceType)
                {
                    throw new ArgumentException($"Duplicated {factory.ServiceType} provider {factory.Name}", nameof(factory));
                }
            }
            _providerFactories.Add(factory);
        }

        public void AddService(IKnpService service)
        {
            _services.Add(service);
        }

        public KnpVirtualBusProvider GetProviderRegistration(IKnpConnectionHost host, int bindingId, KnpVirtualBusRelayType relayType)
        {
            if (_currentProvider is not null)
            {
                throw new InvalidOperationException();
            }

            _peerConnectionCollection.DetachAll();
            _currentProvider = new KnpVirtualBusProvider(this, _services, _providerFactories, host, bindingId, relayType, _logger);
            _peerConnectionCollection.Start();
            return _currentProvider;
        }

        internal void NotifyProviderDisposed()
        {
            if (_currentProvider is null)
            {
                return;
            }

            _currentProvider = null;
        }

        public KnpPeerConnection? GetOrCreatePeerConnection(int sessionId)
        {
            return _peerConnectionCollection.GetOrCreatePeerConnection(sessionId);
        }

    }
}
