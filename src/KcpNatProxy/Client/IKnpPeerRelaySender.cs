using System.Threading;
using System.Threading.Tasks;

namespace KcpNatProxy.Client
{
    internal interface IKnpPeerRelaySender
    {
        int Mss { get; }
        ValueTask RelayPacketAsync(KcpBufferList bufferList, CancellationToken cancellationToken);
        bool RelayPacket(KcpBufferList bufferList);
    }
}
