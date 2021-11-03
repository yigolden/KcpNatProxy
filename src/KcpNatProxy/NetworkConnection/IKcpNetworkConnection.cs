using System;

namespace KcpNatProxy.NetworkConnection
{
    public interface IKcpNetworkConnection : IDisposable
    {
        KcpNetworkConnectionState State { get; }
        int Mss { get; }
        bool Send(KcpBufferList packet);
        KcpNetworkConnectionCallbackRegistration Register(IKcpNetworkConnectionCallback callback);
    }
}
