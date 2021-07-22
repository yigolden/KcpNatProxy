using System;
using System.Threading;
using System.Threading.Tasks;

namespace KcpNatProxy
{
    internal abstract class KcpNatProxyServiceWorker : IDisposable
    {
        public abstract KcpNatProxyServiceType Type { get; }
        public abstract string Name { get;  }
        public abstract int Id { get; }
        public abstract ValueTask InputPacketAsync(ReadOnlyMemory<byte> packet, CancellationToken cancellationToken);
        public abstract void Start();
        public abstract void Dispose();
    }
}
