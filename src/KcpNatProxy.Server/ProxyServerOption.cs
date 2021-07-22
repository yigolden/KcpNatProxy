namespace KcpNatProxy.Server
{
    public class ProxyServerOption
    {
        public string? Listen { get; set; }
        public string? Password { get; set; }
        public int Mtu { get; set; }
    }
}
