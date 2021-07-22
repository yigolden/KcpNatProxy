using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace KcpNatProxy
{
    public sealed class KcpNatProxyServer : IDisposable
    {
        private readonly ILogger _logger;
        private readonly EndPoint _listenEndPoint;
        private readonly byte[]? _password;
        private readonly int _mtu;

        public KcpNatProxyServer(ILogger<KcpNatProxyServer> logger, EndPoint listenEndPoint, byte[]? password, int mtu)
        {
            _logger = logger;
            _listenEndPoint = listenEndPoint ?? throw new ArgumentNullException(nameof(listenEndPoint));
            _password = password;
            _mtu = mtu;
            if (password is not null && password.Length > 64)
            {
                throw new ArgumentException("The length of the password is too long.");
            }
        }

        public async Task RunAsync(CancellationToken cancellationToken)
        {
            using var socket = new Socket(SocketType.Dgram, ProtocolType.Udp);
            SocketHelper.PatchSocket(socket);
            if (_listenEndPoint.Equals(IPAddress.IPv6Any))
            {
                socket.DualMode = true;
            }
            socket.Bind(_listenEndPoint);

            Log.LogServerStartRunning(_logger, _listenEndPoint);

            var factory = new KcpNatProxyServerControllerFactory(_logger, _password, _mtu);
            using UdpSocketServiceDispatcher<KcpNatProxyServerController> dispatcher = UdpSocketServiceDispatcher.Create(socket, factory);
            try
            {
                await dispatcher.RunAsync(_listenEndPoint, GC.AllocateUninitializedArray<byte>(_mtu), cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Ignore
            }
            catch (SocketException ex)
            {
                Log.LogServerSocketException(_logger, ex);
            }
        }

        public void Dispose()
        {

        }

    }
}
