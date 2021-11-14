using System;
using System.Net.Sockets;

namespace KcpNatProxy
{
    internal static class SocketHelper
    {
        private static readonly byte[] _zeroByte = new byte[] { 0 };

        public static void PatchSocket(Socket socket)
        {
            if (OperatingSystem.IsWindows())
            {
                const uint IOC_IN = 0x80000000;
                const uint IOC_VENDOR = 0x18000000;
                const uint SIO_UDP_CONNRESET = IOC_IN | IOC_VENDOR | 12;
                socket.IOControl(unchecked((int)SIO_UDP_CONNRESET), _zeroByte, null);
            }
        }
    }
}
