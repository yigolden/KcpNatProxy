using System.Net;

namespace KcpNatProxy
{
    public sealed class KcpNatProxyServiceDescriptor
    {
        public KcpNatProxyServiceDescriptor(string name, KcpNatProxyServiceType type, IPEndPoint remoteListen, EndPoint localForward, bool noDelay)
        {
            Name = name;
            Type = type;
            RemoteListen = remoteListen;
            LocalForward = localForward;
        }

        public string Name { get; }
        public KcpNatProxyServiceType Type { get; }
        public IPEndPoint RemoteListen { get; }
        public EndPoint LocalForward { get; }
        public bool NoDelay { get; }
    }
}
