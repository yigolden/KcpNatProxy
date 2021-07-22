using System.Collections.Generic;
using System.CommandLine;
using System.CommandLine.Builder;
using System.CommandLine.Invocation;
using System.CommandLine.Parsing;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace KcpNatProxy.Client
{
    internal static class Program
    {
        static async Task<int> Main(string[] args)
        {
            var builder = new CommandLineBuilder();
            builder.Command.Description = "KcpNatProxy client.";

            BuildRootCommand(builder.Command);

            builder.UseVersionOption();

            builder.UseHelp();
            builder.UseSuggestDirective();
            builder.RegisterWithDotnetSuggest();
            builder.UseParseErrorReporting();

            Parser parser = builder.Build();
            return await parser.InvokeAsync(args).ConfigureAwait(false);
        }

        static void BuildRootCommand(Command command)
        {
            command.AddOption(ConfigOption());
            command.Handler = CommandHandler.Create<FileInfo, CancellationToken>(RunAsync);

            Option ConfigOption() =>
                new Option<FileInfo>(new string[] { "--config", "-c" }, "The configuration file.")
                {
                    IsRequired = true
                }.ExistingOnly();
        }

        static async Task RunAsync(FileInfo config, CancellationToken cancellationToken)
        {
            if (config is null || !config.Exists)
            {
                return;
            }

            IHostBuilder builder = Host.CreateDefaultBuilder()
                .ConfigureHostConfiguration(opts =>
                {
                    opts.AddJsonFile(config.FullName, optional: false);
                })
                .ConfigureAppConfiguration(opts =>
                {
                    opts.AddInMemoryCollection(new KeyValuePair<string, string>[]
                    {
                        new KeyValuePair<string, string>("Logging:LogLevel:Microsoft", "Warning"),
#if DEBUG
                        new KeyValuePair<string, string>("Logging:LogLevel:KcpNatProxy", "Debug"),
#endif
                    });
                })
                .ConfigureServices((hostContext, services) =>
                {
                    services.Configure<ProxyClientOption>(hostContext.Configuration);
                    services.AddHostedService<ProxyClientHostedService>();
                })
                .UseSystemd();
            await builder.Build().RunAsync(cancellationToken);
        }
    }
}
