using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using KcpSharp;
using Microsoft.Extensions.Logging;

namespace KcpNatProxy
{
    internal interface IKnpConnectionHost
    {
        IKcpBufferPool BufferPool { get; }
        ILogger Logger { get; }
        int Mtu { get; }
        EndPoint? RemoteEndPoint { get; }
        bool TryGetSessionId(out int sessionId, [NotNullWhen(true)] out byte[]? sessionIdBytes);
        ValueTask SendAsync(KcpBufferList bufferList, CancellationToken cancellationToken);
        bool QueuePacket(KcpBufferList bufferList);
        bool TryRegister(long bindingForwardId, IKnpForwardSession forwardSession);
        bool TryUnregister(long bindingForwardId, IKnpForwardSession forwardSession);
    }
}
