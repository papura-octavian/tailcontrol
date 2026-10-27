using TailControl.Agent;
using TailControl.Shared.AspNetCore;

var contentRoot = AppContext.BaseDirectory;
var environment = Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT")
    ?? Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT")
    ?? "Production";
var isDevelopment = string.Equals(environment, "Development", StringComparison.OrdinalIgnoreCase);

// Comenzile de administrare (authorize-client etc.) nu pornesc serverul.
if (AuthorizedClientsCli.IsCommand(args))
{
    var cliConfig = new ConfigurationBuilder()
        .SetBasePath(contentRoot)
        .AddJsonFile("appsettings.json", optional: true)
        .AddJsonFile($"appsettings.{environment}.json", optional: true)
        .Build();
    return AuthorizedClientsCli.Run(args, AgentOptions.Load(cliConfig).ToCliContext(isDevelopment), Console.Out, TimeProvider.System);
}

var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = args, ContentRootPath = contentRoot });
builder.Host.UseWindowsService(o => o.ServiceName = AgentOptions.ServiceName);

var options = AgentOptions.Load(builder.Configuration);
TailscaleHosting.ValidateConfiguration(builder.Configuration, builder.Environment.IsDevelopment());

var listen = options.ToListenSettings();
var addresses = await TailscaleHosting.ResolveListenAddressesAsync(listen, builder.Environment.IsDevelopment());
builder.WebHost.ConfigureKestrel(k => TailscaleHosting.ConfigureKestrel(k, listen, addresses));
builder.Services.AddAgentServices(options);

var app = builder.Build();
app.UseAgentPipeline();

var listenAddresses = string.Join(", ", addresses);
StartupLog.Started(app.Logger, listenAddresses, options.Port, options.HasCertificate ? "HTTPS" : "HTTP, fara certificat");
if (options.DryRun)
{
    StartupLog.DryRun(app.Logger);
}
else
{
    StartupLog.LiveMode(app.Logger);
}

await app.RunAsync();
return 0;

internal static partial class StartupLog
{
    [LoggerMessage(Level = LogLevel.Information, Message = "Agent pornit pe {Addresses}, port {Port} ({Scheme})")]
    public static partial void Started(ILogger logger, string addresses, int port, string scheme);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Mod DryRun: comenzile sunt doar logate, nu se executa nimic.")]
    public static partial void DryRun(ILogger logger);

    [LoggerMessage(Level = LogLevel.Warning, Message = "DryRun este dezactivat: comenzile vor fi executate pe aceasta masina.")]
    public static partial void LiveMode(ILogger logger);
}
