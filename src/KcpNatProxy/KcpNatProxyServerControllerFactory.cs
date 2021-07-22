using System.Net;
using Microsoft.Extensions.Logging;

namespace KcpNatProxy
{
    internal sealed class KcpNatProxyServerControllerFactory : UdpServerDispatcherOptions<KcpNatProxyServerController>
    {
        private readonly ListenEndPointTracker _epTracker;
        private readonly ILogger _logger;
        private readonly byte[]? _password;
        private readonly int _mtu;
        private readonly MemoryPool _memoryPool;

        public KcpNatProxyServerControllerFactory(ILogger logger, byte[]? password, int mtu)
        {
            _epTracker = new ListenEndPointTracker();
            _logger = logger;
            _password = password;
            _mtu = mtu;
            _memoryPool = new MemoryPool(mtu);
        }

        public override KcpNatProxyServerController Activate(IUdpServiceDispatcher dispatcher, EndPoint endpoint)
        {
            var controller = new KcpNatProxyServerController(dispatcher, endpoint, _epTracker, _password, _mtu, _memoryPool, _logger);
            controller.Start();
            return controller;
        }

        public override void Close(KcpNatProxyServerController service)
        {
            service.Dispose();
        }
    }
}
