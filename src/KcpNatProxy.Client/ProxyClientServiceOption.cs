namespace KcpNatProxy.Client
{
    public class ProxyClientServiceOption
    {
        public string? Name { get; set; }
        public string? Type { get; set; }
        public string? RemoteListen { get; set; }
        public string? LocalForward { get; set; }
        public bool NoDelay { get; set; }
    }
}
