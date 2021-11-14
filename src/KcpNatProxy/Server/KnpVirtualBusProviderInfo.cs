using System;

namespace KcpNatProxy.Server
{
    internal class KnpVirtualBusProviderInfo
    {
        private readonly KnpRentedInt32 _bindingId;

        public KnpVirtualBusProviderInfo(int sessionId, string name, KnpServiceType serviceType, KnpRentedInt32 bindingId, ReadOnlySpan<byte> parameters)
        {
            SessionId = sessionId;
            Name = name;
            ServiceType = serviceType;
            _bindingId = bindingId;
            Parameters = parameters.IsEmpty ? Array.Empty<byte>() : parameters.ToArray();
        }

        public int SessionId { get; }
        public string Name { get; }
        public KnpServiceType ServiceType { get; }
        public byte[] Parameters { get; }
        public int BindingId => _bindingId.Value;

        public void ReturnBindingId()
        {
            _bindingId.Dispose();
        }
    }
}
