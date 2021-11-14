using KcpSharp;

namespace KcpNatProxy.NetworkConnection
{
    public sealed class KcpNetworkConnectionOptions
    {
        public IKcpBufferPool? BufferPool { get; set; }
        public int Mtu { get; set; }
    }
}
