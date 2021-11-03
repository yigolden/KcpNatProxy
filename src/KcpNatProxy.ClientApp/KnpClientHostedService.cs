using KcpNatProxy.Client;
using Microsoft.Extensions.Options;

namespace KcpNatProxy.ClientApp
{
    public sealed class KnpClientHostedService : BackgroundService
    {
        private readonly KnpClientOptions _options;
        private readonly ILoggerFactory _loggerFactory;

        public KnpClientHostedService(IOptions<KnpClientOptions> optionsAccessor, ILoggerFactory loggerFactory)
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
            var client = new KnpClient(_options, _loggerFactory.CreateLogger<KnpClient>());
            return client.RunAsync(stoppingToken);
        }
    }
}
