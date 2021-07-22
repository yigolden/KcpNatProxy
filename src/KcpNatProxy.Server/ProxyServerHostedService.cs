using System;
using System.Diagnostics;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace KcpNatProxy.Server
{
    public sealed partial class ProxyServerHostedService : BackgroundService
    {
        private readonly ILoggerFactory _loggerFactory;
        private readonly IHostApplicationLifetime _applicationLifetime;
        private readonly ProxyServerOption _options;
        private readonly ILogger _logger;

        public ProxyServerHostedService(ILoggerFactory loggerFactory, IHostApplicationLifetime applicationLifetime, IOptions<ProxyServerOption> options)
        {
            _loggerFactory = loggerFactory;
            _applicationLifetime = applicationLifetime;
            _options = options?.Value ?? throw new InvalidOperationException();
            _logger = loggerFactory.CreateLogger<ProxyServerHostedService>();
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            KcpNatProxyServer? serverService = null;
            try
            {
                // Validate configuration
                bool validationCompleted = false;
                do
                {
                    // Validate EndPoint and Password
                    if (string.IsNullOrEmpty(_options.Listen))
                    {
                        LogConfigEndPointMissing();
                        break;
                    }
                    if (!IPEndPoint.TryParse(_options.Listen, out IPEndPoint? ipEndPoint))
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
                                LogConfigPasswordNoneAscii();
                                break;
                            }
                        }
                    }

                    serverService = new KcpNatProxyServer(_loggerFactory.CreateLogger<KcpNatProxyServer>(), ipEndPoint, string.IsNullOrEmpty(password) ? null : Encoding.ASCII.GetBytes(password), mtu);

                    validationCompleted = true;
                } while (false);

                if (!validationCompleted)
                {
                    _applicationLifetime.StopApplication();
                    return;
                }

                Debug.Assert(serverService is not null);

                await serverService.RunAsync(stoppingToken);
            }
            finally
            {
                serverService?.Dispose();
            }

        }


        [LoggerMessage(EventId = 0x2001, Level = LogLevel.Critical, Message = "Service failed to start because Listen is not provided in the configuration.")]
        private partial void LogConfigEndPointMissing();

        [LoggerMessage(EventId = 0x2002, Level = LogLevel.Critical, Message = "Service failed to start because Listen is not a valid IPEndPoint in the configuration.")]
        private partial void LogConfigEndPointInvalid();

        [LoggerMessage(EventId = 0x2003, Level = LogLevel.Critical, Message = "Service failed to start because Password is too long. Its length should not be larger than 36.")]
        private partial void LogConfigPasswordTooLong();

        [LoggerMessage(EventId = 0x2004, Level = LogLevel.Critical, Message = "Service failed to start because Password contains non-ASCII characters.")]
        private partial void LogConfigPasswordNoneAscii();

        [LoggerMessage(EventId = 0x2005, Level = LogLevel.Critical, Message = "Service failed to start because invalid MTU value is provided in the configuration.")]
        private partial void LogConfigInvalidMtu();
    }
}
