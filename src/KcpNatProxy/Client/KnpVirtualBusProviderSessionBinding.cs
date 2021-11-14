using System;
using System.Diagnostics.CodeAnalysis;

namespace KcpNatProxy.Client
{
    internal readonly struct KnpVirtualBusProviderSessionBinding : IEquatable<KnpVirtualBusProviderSessionBinding>
    {
        private readonly byte[] _parameters;
        private readonly int _sessionId;
        private readonly int _bindingId;

        public byte[] Parameters => _parameters ?? Array.Empty<byte>();
        public int SessionId => _sessionId;
        public int BindingId => _bindingId;

        public bool IsEmpty => _sessionId == 0 && _bindingId == 0;

        public KnpVirtualBusProviderSessionBinding(int sessionId, int bindingId, byte[] parameters)
        {
            _parameters = parameters;
            _sessionId = sessionId;
            _bindingId = bindingId;
        }

        public bool Equals(KnpVirtualBusProviderSessionBinding other)
        {
            return _sessionId == other._sessionId && _bindingId == other._bindingId;
        }

        public override bool Equals([NotNullWhen(true)] object? obj) => obj is KnpVirtualBusProviderSessionBinding other && Equals(other);

        public override int GetHashCode()
        {
            return HashCode.Combine(_sessionId, _bindingId);
        }
    }
}
