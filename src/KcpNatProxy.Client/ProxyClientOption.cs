namespace KcpNatProxy.Client
{
    public class ProxyClientOption
    {
        public string? EndPoint { get; set; }
        public string? Password { get; set; }
        public int Mtu { get; set; }
#pragma warning disable CA1819 // Properties should not return arrays
        public ProxyClientServiceOption[]? Services { get; set; }
#pragma warning restore CA1819 // Properties should not return arrays

    }
}
