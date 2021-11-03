using System.Threading;
using System.Threading.Tasks;

namespace KcpNatProxy
{
    internal interface IKnpForwardHost
    {
        int BindingId { get; }
        int Mss { get; }
        ValueTask ForwardBackAsync(KcpBufferList bufferList, CancellationToken cancellationToken);
        void NotifySessionClosed(int forwardId, IKnpForwardSession session);
    }
}
