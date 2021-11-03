using KcpNatProxy.Server;
using Microsoft.Extensions.Options;

namespace KcpNatProxy.ServerApp
{
    public sealed class KnpServerHostedService : BackgroundService
    {
        private readonly KnpServerOptions _options;
        private readonly ILoggerFactory _loggerFactory;

        public KnpServerHostedService(IOptions<KnpServerOptions> optionsAccessor, ILoggerFactory loggerFactory)
        {
            if (optionsAccessor is null)
            {
                throw new ArgumentNullException(nameof(optionsAccessor));
            }

            _options = optionsAccessor.Value;
            _loggerFactory = loggerFactory;
        }

        protected override Task ExecuteAsync(CancellationToken stoppingToken)
        {
            var server = new KnpServer(_options, _loggerFactory.CreateLogger<KnpServer>());
            return server.RunAsync(stoppingToken);
        }

    }
}
