using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace KcpNatProxy
{
    internal static class SocketHelper
    {
        public static void PatchSocket(Socket socket)
        {
            if (OperatingSystem.IsWindows())
            {
                uint IOC_IN = 0x80000000;
                uint IOC_VENDOR = 0x18000000;
                uint SIO_UDP_CONNRESET = IOC_IN | IOC_VENDOR | 12;
                socket.IOControl((int)SIO_UDP_CONNRESET, new byte[] { Convert.ToByte(false) }, null);
            }
        }

        public static async Task SendToAndLogErrorAsync(IUdpServiceDispatcher sender, EndPoint remoteEndPoint, ILogger logger, ReadOnlyMemory<byte> packet, CancellationToken cancellationToken)
        {
            try
            {
                await sender.SendPacketAsync(remoteEndPoint, packet, cancellationToken).ConfigureAwait(false);

            }
            catch (OperationCanceledException)
            {
                // Ignore
            }
            catch (ObjectDisposedException)
            {
                // Ignore
            }
            catch (Exception ex)
            {
                Log.LogServerSocketSendError(logger, ex);
            }
        }
    }
}
