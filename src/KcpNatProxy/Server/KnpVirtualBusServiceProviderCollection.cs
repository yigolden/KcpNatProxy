using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;

namespace KcpNatProxy.Server
{
    internal sealed class KnpVirtualBusServiceProviderCollection : IDisposable
    {
        private readonly KnpInt32IdAllocator _bindingIdAllocator;
        private readonly ConcurrentDictionary<ServiceDescriptor, ProviderList> _providers = new();
        private bool _disposed;

        public KnpVirtualBusServiceProviderCollection(KnpInt32IdAllocator bindingIdAllocator)
        {
            _bindingIdAllocator = bindingIdAllocator;
        }

        public KnpVirtualBusProviderInfo? TryAddProvider(int sessionId, string name, KnpServiceType serviceType, ReadOnlySpan<byte> parameters)
        {
            if (_disposed)
            {
                return null;
            }
            ProviderList providerList = _providers.GetOrAdd(new ServiceDescriptor(name, serviceType), sd => new ProviderList());
            if (_disposed)
            {
                _providers.TryRemove(new KeyValuePair<ServiceDescriptor, ProviderList>());
                return null;
            }

            var providerInfo = new KnpVirtualBusProviderInfo(sessionId, name, serviceType, _bindingIdAllocator.Allocate(), parameters);
            if (!providerList.Add(providerInfo))
            {
                providerInfo.ReturnBindingId();
                return null;
            }

            return providerInfo;
        }

        public void RemoveProvider(KnpVirtualBusProviderInfo provider)
        {
            if (_disposed)
            {
                return;
            }
            if (_providers.TryGetValue(new ServiceDescriptor(provider.Name, provider.ServiceType), out ProviderList? providerList))
            {
                providerList.Remove(provider);
            }
        }

        public void RemoveForServiceBinding(int sessionId)
        {
            if (_disposed)
            {
                return;
            }
            foreach (KeyValuePair<ServiceDescriptor, ProviderList> item in _providers)
            {
                item.Value.RemoveForServiceBinding(sessionId);
            }
        }

        public KnpVirtualBusProviderInfo? QueryProvider(string name, KnpServiceType serviceType)
        {
            if (_disposed)
            {
                return null;
            }
            if (_providers.TryGetValue(new ServiceDescriptor(name, serviceType), out ProviderList? providerList))
            {
                return providerList.GetAvailable();
            }
            return null;
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;

            KnpCollectionHelper.ClearAndDispose(_providers);
        }

        readonly struct ServiceDescriptor : IEquatable<ServiceDescriptor>
        {
            private readonly string _name;
            private readonly KnpServiceType _serviceType;

            public ServiceDescriptor(string name, KnpServiceType serviceType)
            {
                _name = name;
                _serviceType = serviceType;
            }

            public bool Equals(ServiceDescriptor other) => _serviceType == other._serviceType && string.Equals(_name, other._name, StringComparison.Ordinal);
            public override bool Equals([NotNullWhen(true)] object? obj) => obj is ServiceDescriptor other && Equals(other);
            public override int GetHashCode() => _name is null ? 0 : HashCode.Combine(string.GetHashCode(_name, StringComparison.Ordinal), _serviceType);
        }

        class ProviderList : IDisposable
        {
            private SimpleLinkedList<KnpVirtualBusProviderInfo> _providers = new();
            private bool _disposed;

            public bool Add(KnpVirtualBusProviderInfo provider)
            {
                if (_disposed)
                {
                    return false;
                }
                lock (_providers)
                {
                    if (_disposed)
                    {
                        return false;
                    }
                    _providers.AddLast(new SimpleLinkedListNode<KnpVirtualBusProviderInfo>(provider));
                }
                return true;
            }

            public bool Remove(KnpVirtualBusProviderInfo provider)
            {
                if (_disposed)
                {
                    return false;
                }
                lock (_providers)
                {
                    SimpleLinkedListNode<KnpVirtualBusProviderInfo>? node = _providers.First;
                    while (node is not null)
                    {
                        if (ReferenceEquals(node.Value, provider))
                        {
                            provider.ReturnBindingId();
                            _providers.Remove(node);
                            return true;
                        }
                        node = node.Next;
                    }
                }
                return false;
            }

            public void RemoveForServiceBinding(int sessionId)
            {
                if (_disposed)
                {
                    return;
                }
                lock (_providers)
                {
                    SimpleLinkedListNode<KnpVirtualBusProviderInfo>? node = _providers.First;
                    SimpleLinkedListNode<KnpVirtualBusProviderInfo>? nextNode = node?.next;
                    while (node is not null)
                    {
                        if (node.Value.SessionId == sessionId)
                        {
                            node.Value.ReturnBindingId();
                            _providers.Remove(node);
                        }
                        node = nextNode;
                        nextNode = node?.Next;
                    }
                }
            }

            public KnpVirtualBusProviderInfo? GetAvailable()
            {
                if (_disposed)
                {
                    return null;
                }
                lock (_providers)
                {
                    return _providers.First?.Value;
                }
            }

            public void Dispose()
            {
                if (_disposed)
                {
                    return;
                }
                lock (_providers)
                {
                    _disposed = true;
                    SimpleLinkedListNode<KnpVirtualBusProviderInfo>? node = _providers.First;
                    SimpleLinkedListNode<KnpVirtualBusProviderInfo>? nextNode = node?.next;
                    while (node is not null)
                    {
                        node.Value.ReturnBindingId();
                        _providers.Remove(node);
                        node = nextNode;
                        nextNode = node?.Next;
                    }
                }

            }
        }
    }
}
