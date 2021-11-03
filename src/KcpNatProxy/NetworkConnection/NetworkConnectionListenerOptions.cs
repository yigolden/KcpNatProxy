using KcpSharp;

namespace KcpNatProxy.NetworkConnection
{
    public sealed class NetworkConnectionListenerOptions
    {
        public IKcpBufferPool? BufferPool { get; set; }
        public int Mtu { get; set; } = 1400;
        public int BackLog { get; set; } = 128;
    }
}
