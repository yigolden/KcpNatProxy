using System.Threading;
using System.Threading.Tasks;

namespace KcpNatProxy.Client
{
    internal interface IKnpPeerConnectionSender
    {
        int Mss { get; }
        ValueTask SendAsync(KcpBufferList buffer, CancellationToken cancellationToken);
        bool QueuePacket(KcpBufferList buffer);
    }
}
