using System;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace KcpNatProxy.Client
{
    public sealed partial class ProxyClientHostedService : BackgroundService
    {
        private readonly ILoggerFactory _loggerFactory;
        private readonly IHostApplicationLifetime _applicationLifetime;
        private readonly ProxyClientOption _options;
        private readonly ILogger _logger;

        public ProxyClientHostedService(ILoggerFactory loggerFactory, IHostApplicationLifetime applicationLifetime, IOptions<ProxyClientOption> options)
        {
            _loggerFactory = loggerFactory;
            _applicationLifetime = applicationLifetime;
            _options = options?.Value ?? throw new InvalidOperationException();
            _logger = loggerFactory.CreateLogger<ProxyClientHostedService>();
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            KcpNatProxyClient? clientService = null;
            try
            {
                // Validate configuration
                bool validationCompleted = false;
                do
                {
                    // Validate EndPoint and Password
                    if (string.IsNullOrEmpty(_options.EndPoint))
                    {
                        LogConfigEndPointMissing();
                        break;
                    }
                    if (!TryParseEndPoint(_options.EndPoint, out EndPoint? ipEndPoint))
                    {
                        LogConfigEndPointInvalid();
                        break;
                    }
                    int mtu = _options.Mtu;
                    if (mtu == 0)
                    {
                        mtu = 1428;
                    }
                    else if (mtu < 256 || mtu > 60000)
                    {
                        LogConfigInvalidMtu();
                    }
                    string? password = _options.Password;
                    if (password is not null)
                    {
                        if (password.Length > 36)
                        {
                            LogConfigPasswordTooLong();
                            break;
                        }
                        foreach (char c in password)
                        {
                            if (c < 0x20 || c > 0x7e)
                            {
                                LogConfigPasswordNonAscii();
                                break;
                            }
                        }
                    }

                    clientService = new KcpNatProxyClient(_loggerFactory.CreateLogger<KcpNatProxyClient>(), ipEndPoint, string.IsNullOrEmpty(password) ? null : Encoding.ASCII.GetBytes(password), mtu);

                    // Validate services
                    ProxyClientServiceOption[]? services = _options.Services;
                    if (services is null || services.Length == 0)
                    {
                        LogConfigServicesMissing();
                        break;

                    }
                    int i = 0;
                    for (; i < services.Length; i++)
                    {
                        ProxyClientServiceOption? service = services[i];
                        if (service is null || string.IsNullOrEmpty(service.Name))
                        {
                            LogConfigServiceNameMissing(i);
                            break;
                        }
                        if (service.Name.Length > 64)
                        {
                            LogConfigServiceNameTooLong(i);
                            break;
                        }
                        foreach (char c in service.Name)
                        {
                            if (c < 0x20 || c > 0x7e)
                            {
                                LogConfigServiceNameNonAscii(i);
                                break;
                            }
                        }
                        if (string.IsNullOrEmpty(service.Type))
                        {
                            LogConfigServiceTypeMissing(i);
                            break;
                        }
                        KcpNatProxyServiceType type;
                        if (string.Equals(service.Type, "tcp", StringComparison.Ordinal))
                        {
                            type = KcpNatProxyServiceType.Tcp;
                        }
                        else if (string.Equals(service.Type, "udp", StringComparison.Ordinal))
                        {
                            type = KcpNatProxyServiceType.Udp;
                        }
                        else
                        {
                            LogConfigServiceTypeInvalid(i);
                            break;
                        }
                        if (string.IsNullOrEmpty(service.RemoteListen))
                        {
                            LogConfigServiceRemoteListenMissing(i);
                            break;
                        }
                        if (!IPEndPoint.TryParse(service.RemoteListen, out IPEndPoint? remoteListen) || (remoteListen.AddressFamily != AddressFamily.InterNetwork && remoteListen.AddressFamily != AddressFamily.InterNetworkV6))
                        {
                            LogConfigServiceRemoteListenInvalid(i);
                            break;
                        }
                        if (string.IsNullOrEmpty(service.LocalForward))
                        {
                            LogConfigServiceLocalForwardMissing(i);
                            break;
                        }
                        if (!IPEndPoint.TryParse(service.LocalForward, out IPEndPoint? localForward))
                        {
                            LogConfigServiceLocalForwardInvalid(i);
                            break;
                        }

                        clientService.AddService(service.Name, type, remoteListen, localForward, service.NoDelay);
                    }
                    if (i < services.Length)
                    {
                        break;
                    }

                    validationCompleted = true;
                } while (false);

                if (!validationCompleted)
                {
                    _applicationLifetime.StopApplication();
                    return;
                }

                Debug.Assert(clientService is not null);

                await clientService.RunAsync(stoppingToken);
            }
            finally
            {
                clientService?.Dispose();
            }
        }

        private static bool TryParseEndPoint(ReadOnlySpan<char> expression, [NotNullWhen(true)] out EndPoint? endPoint)
        {
            if (expression.Length < 3)
            {
                endPoint = null;
                return false;
            }
            int pos = expression.LastIndexOf(':');
            if (pos <= 0)
            {
                endPoint = null;
                return false;
            }
            ReadOnlySpan<char> portPart = expression.Slice(pos + 1);
            if (portPart.IsEmpty)
            {
                endPoint = null;
                return false;
            }
            if (!ushort.TryParse(portPart, NumberStyles.None, CultureInfo.InvariantCulture, out ushort port))
            {
                endPoint = null;
                return false;
            }
            ReadOnlySpan<char> domainPart = expression.Slice(0, pos);
            if (domainPart.IsEmpty)
            {
                endPoint = null;
                return false;
            }
            if (IPAddress.TryParse(domainPart, out IPAddress? address))
            {
                endPoint = new IPEndPoint(address, port);
            }
            else
            {
                endPoint = new DnsEndPoint(domainPart.ToString(), port);
            }
            return true;
        }

        [LoggerMessage(EventId = 0x1001, Level = LogLevel.Critical, Message = "Service failed to start because EndPoint is not provided in the configuration.")]
        private partial void LogConfigEndPointMissing();

        [LoggerMessage(EventId = 0x1002, Level = LogLevel.Critical, Message = "Service failed to start because EndPoint is not a valid IPEndPoint or DnsEndPoint in the configuration.")]
        private partial void LogConfigEndPointInvalid();

        [LoggerMessage(EventId = 0x1003, Level = LogLevel.Critical, Message = "Service failed to start because Password is too long. Its length should not be larger than 36.")]
        private partial void LogConfigPasswordTooLong();

        [LoggerMessage(EventId = 0x1004, Level = LogLevel.Critical, Message = "Service failed to start because Password contains non-ASCII characters.")]
        private partial void LogConfigPasswordNonAscii();

        [LoggerMessage(EventId = 0x1005, Level = LogLevel.Critical, Message = "Service failed to start because no services is configured in the configuration.")]
        private partial void LogConfigServicesMissing();

        [LoggerMessage(EventId = 0x1006, Level = LogLevel.Critical, Message = "Service failed to start because service name is not provided. The index of the service is `{index}`")]
        private partial void LogConfigServiceNameMissing(int index);

        [LoggerMessage(EventId = 0x1007, Level = LogLevel.Critical, Message = "Service failed to start because the service name is too long. It should be no longer than 64 characters. The index of the service is `{index}`")]
        private partial void LogConfigServiceNameTooLong(int index);

        [LoggerMessage(EventId = 0x1008, Level = LogLevel.Critical, Message = "Service failed to start because the service name contains non-ASCII characters. The index of the service is `{index}`")]
        private partial void LogConfigServiceNameNonAscii(int index);

        [LoggerMessage(EventId = 0x1009, Level = LogLevel.Critical, Message = "Service failed to start because service type is not provided. The index of the service is `{index}`")]
        private partial void LogConfigServiceTypeMissing(int index);

        [LoggerMessage(EventId = 0x100A, Level = LogLevel.Critical, Message = "Service failed to start because service type is invalid. The valid types are 'tcp' or 'udp'. The index of the service is `{index}`")]
        private partial void LogConfigServiceTypeInvalid(int index);

        [LoggerMessage(EventId = 0x100B, Level = LogLevel.Critical, Message = "Service failed to start because the remote listening endpoint of service is not provided. The index of the service is `{index}`")]
        private partial void LogConfigServiceRemoteListenMissing(int index);

        [LoggerMessage(EventId = 0x100C, Level = LogLevel.Critical, Message = "Service failed to start because the remote listening endpoint of service is not a valid IPv4 or IPv6 IPEndPoint. The index of the service is `{index}`")]
        private partial void LogConfigServiceRemoteListenInvalid(int index);

        [LoggerMessage(EventId = 0x100D, Level = LogLevel.Critical, Message = "Service failed to start because the local forwarding endpoint of service is not provided. The index of the service is `{index}`")]
        private partial void LogConfigServiceLocalForwardMissing(int index);

        [LoggerMessage(EventId = 0x100E, Level = LogLevel.Critical, Message = "Service failed to start because the local forwarding endpoint of service is not a valid IPEndPoint. The index of the service is `{index}`")]
        private partial void LogConfigServiceLocalForwardInvalid(int index);

        [LoggerMessage(EventId = 0x100F, Level = LogLevel.Critical, Message = "Service failed to start because invalid MTU value is provided in the configuration.")]
        private partial void LogConfigInvalidMtu();
    }
}
