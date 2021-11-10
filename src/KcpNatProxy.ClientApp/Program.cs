using System.CommandLine;
using System.CommandLine.Builder;
using System.CommandLine.Invocation;
using System.CommandLine.Parsing;
using KcpNatProxy.Client;
using KcpNatProxy.ClientApp;

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


static void BuildRootCommand(Command command)
{
    command.AddOption(ConfigOption());
    command.AddOption(VerboseOption());
    command.AddOption(TraceOption());
    command.Handler = CommandHandler.Create<FileInfo, bool, bool, CancellationToken>(RunAsync);

    Option ConfigOption() =>
        new Option<FileInfo>(new string[] { "--config", "-c" }, "The configuration file.")
        {
            IsRequired = true
        }.ExistingOnly();

    Option VerboseOption() =>
        new Option<bool>(new string[] { "--verbose", "-v" }, "Enable verbose logging.")
        {
            Arity = ArgumentArity.ZeroOrOne
        };

    Option TraceOption() =>
        new Option<bool>(new string[] { "--trace", "-t" }, "Enable trace level logging. (very verbose)")
        {
            Arity = ArgumentArity.ZeroOrOne,
#if !WITH_TRACE_LOGGING
            IsHidden = true
#endif
        };
}

static async Task RunAsync(FileInfo config, bool verbose, bool trace, CancellationToken cancellationToken)
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
        .ConfigureLogging(opts =>
        {
            opts.AddSimpleConsole(options =>
            {
                options.SingleLine = true;
                options.TimestampFormat = "[yyyy-MM-dd HH:mm:ss.fff] ";
            });
        })
        .ConfigureAppConfiguration(opts =>
        {
            string loglevel = trace ? "Trace" : (verbose ? "Debug" : "Information");
            opts.AddInMemoryCollection(new KeyValuePair<string, string>[]
            {
                new KeyValuePair<string, string>("Logging:LogLevel:Microsoft", "Warning"),
                new KeyValuePair<string, string>("Logging:LogLevel:KcpNatReverseProxy", loglevel),
            });
        })
        .ConfigureServices((hostContext, services) =>
        {
            services.Configure<KnpClientOptions>(hostContext.Configuration);
            services.AddHostedService<KnpClientHostedService>();
        })
        .UseSystemd();
    if (OperatingSystem.IsWindows())
    {
        builder = builder.UseWindowsService();
    }
    await builder.Build().RunAsync(cancellationToken).ConfigureAwait(false);
}
