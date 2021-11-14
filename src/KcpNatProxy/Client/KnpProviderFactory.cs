using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using KcpSharp;

namespace KcpNatProxy.Client
{
    internal abstract class KnpProviderFactory
    {
        public abstract Task<bool> RegisterAsync(KcpConversation channel, IKnpConnectionHost consumer, ConcurrentDictionary<int, IKnpProvider> providers, CancellationToken cancellationToken);
    }
}
