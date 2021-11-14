using System.Net;
using KcpNatProxy.NetworkConnection;

namespace KcpNatProxy.Client
{
    internal interface IKnpPeerConnectionReceiverPeerCallback
    {
        void OnTransportClosed();
        bool OnPeerProbing(int sessionId, EndPoint remoteEndPoint, KcpNetworkConnection networkConnection);
    }
}
