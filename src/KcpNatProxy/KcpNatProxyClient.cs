using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace KcpNatProxy
{
    public sealed class KcpNatProxyClient : IDisposable
    {
        private readonly ILogger _logger;
        private readonly EndPoint _remoteEndPoint;
        private readonly byte[]? _password;
        private readonly int _mtu;
        private readonly MemoryPool _memoryPool;
        private List<KcpNatProxyServiceDescriptor> _services;

        public KcpNatProxyClient(ILogger<KcpNatProxyClient> loggerFactory, EndPoint remoteEndPoint, byte[]? password, int mtu)
        {
            _logger = loggerFactory;
            _remoteEndPoint = remoteEndPoint ?? throw new ArgumentNullException(nameof(remoteEndPoint));
            if (password is not null && password.Length > 64)
            {
                throw new ArgumentException("The length of the password is too long.");
            }
            _password = password;
            _mtu = mtu;
            _memoryPool = new MemoryPool(mtu);
            _services = new List<KcpNatProxyServiceDescriptor>();
        }

        public void AddService(string name, KcpNatProxyServiceType type, IPEndPoint remoteListen, EndPoint localForward, bool noDelay)
        {
            _services.Add(new KcpNatProxyServiceDescriptor(name, type, remoteListen, localForward, noDelay));
        }

        public async Task RunAsync(CancellationToken cancellationToken)
        {
            _services = new List<KcpNatProxyServiceDescriptor>(_services);

            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    TimeSpan waitTime;
                    {
                        // resolve
                        EndPoint remoteEndPoint = _remoteEndPoint;
                        IPEndPoint? secondaryEndPoint = null;
                        Debug.Assert(remoteEndPoint is IPEndPoint || remoteEndPoint is DnsEndPoint);
                        if (remoteEndPoint is DnsEndPoint dnsEndPoint)
                        {
                            IPAddress[] ipaddrs = await Dns.GetHostAddressesAsync(dnsEndPoint.Host, cancellationToken).ConfigureAwait(false);
                            IPAddress? ipv6Addr = ipaddrs.FirstOrDefault(ip => ip.AddressFamily == AddressFamily.InterNetworkV6);
                            IPAddress? ipv4Addr = ipaddrs.FirstOrDefault(ip => ip.AddressFamily == AddressFamily.InterNetwork);
                            if (ipv4Addr is null && ipv6Addr is null)
                            {
                                Log.LogClientNoIPAddress(_logger, dnsEndPoint.Host);
                                await Task.Delay(TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);
                                continue;
                            }
                            if (ipv6Addr is not null)
                            {
                                remoteEndPoint = new IPEndPoint(ipv6Addr, dnsEndPoint.Port);
                                if (ipv4Addr is not null)
                                {
                                    secondaryEndPoint = new IPEndPoint(ipv4Addr, dnsEndPoint.Port);
                                }
                            }
                            else
                            {
                                Debug.Assert(ipv4Addr is not null);
                                remoteEndPoint = new IPEndPoint(ipv4Addr, dnsEndPoint.Port);
                            }
                        }

                        var socket = new Socket(SocketType.Dgram, ProtocolType.Udp);
                        try
                        {
                            bool isConnected = false;
                            // Try IPv6
                            try
                            {
                                Log.LogClientConnecting(_logger, remoteEndPoint);
                                await socket.ConnectAsync(remoteEndPoint, cancellationToken).ConfigureAwait(false);
                                isConnected = true;
                            }
                            catch (SocketException ex)
                            {
                                if (secondaryEndPoint is null)
                                {
                                    Log.LogClientConnectionFailedCritical(_logger, ex, remoteEndPoint);
                                }
                                else
                                {
                                    Log.LogClientConnectionFailed(_logger, ex, remoteEndPoint);
                                }
                            }
                            if (!isConnected && secondaryEndPoint is not null)
                            {
                                remoteEndPoint = secondaryEndPoint;
                                try
                                {
                                    Log.LogClientConnecting(_logger, remoteEndPoint);
                                    await socket.ConnectAsync(remoteEndPoint, cancellationToken).ConfigureAwait(false);
                                    isConnected = true;
                                }
                                catch (SocketException ex)
                                {
                                    Log.LogClientConnectionFailedCritical(_logger, ex, remoteEndPoint);
                                }
                            }
                            if (!isConnected)
                            {
                                await Task.Delay(TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);
                                continue;
                            }

                            using var controller = new KcpNatProxyClientController(socket, remoteEndPoint, _password, _services, _mtu, _memoryPool, _logger);
                            waitTime = await controller.RunAsync(cancellationToken).ConfigureAwait(false);
                        }
                        finally
                        {
                            socket.Dispose();
                        }
                    }
                    cancellationToken.ThrowIfCancellationRequested();
                    Log.LogClientTryAgain(_logger, waitTime.TotalSeconds);
                    await Task.Delay(waitTime, cancellationToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                // Ignore 
            }
        }

        public void Dispose()
        {

        }

    }
}
