using TailControl.Agent;
using TailControl.Shared.AspNetCore;

var contentRoot = AppContext.BaseDirectory;
var environment = Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT")
    ?? Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT")
    ?? "Production";
var isDevelopment = string.Equals(environment, "Development", StringComparison.OrdinalIgnoreCase);

// Configurarea masinii (AllowedHosts, DryRun, certificat) e scrisa de installer in config\agent.json,
// scriibil doar de administratori, ca sa supravietuiasca actualizarilor. Nu e citita in Development:
// pe o masina cu agentul instalat, o rulare de dezvoltare ar mosteni altfel DryRun=false.
var siteConfig = isDevelopment ? null : AgentOptions.SiteConfigPath();

if (args is ["prepare-directories"])
{
    return AgentSetup.PrepareDirectories(LoadCliOptions(), isDevelopment, Console.Out);
}

// Comenzile de administrare (authorize-client etc.) nu pornesc serverul.
if (AuthorizedClientsCli.IsCommand(args))
{
    return AuthorizedClientsCli.Run(args, LoadCliOptions().ToCliContext(isDevelopment), Console.Out, TimeProvider.System);
}

var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = args, ContentRootPath = contentRoot });
if (siteConfig is not null)
{
    builder.Configuration.AddJsonFile(siteConfig, optional: true);
}

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

app.LogAuthorizedClientsState();

await app.RunAsync();
return 0;

AgentOptions LoadCliOptions()
{
    var config = new ConfigurationBuilder()
        .SetBasePath(contentRoot)
        .AddJsonFile("appsettings.json", optional: true)
        .AddJsonFile($"appsettings.{environment}.json", optional: true);
    if (siteConfig is not null)
    {
        config.AddJsonFile(siteConfig, optional: true);
    }

    return AgentOptions.Load(config.Build());
}

internal static partial class StartupLog
{
    [LoggerMessage(Level = LogLevel.Information, Message = "Agent pornit pe {Addresses}, port {Port} ({Scheme})")]
    public static partial void Started(ILogger logger, string addresses, int port, string scheme);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Mod DryRun: comenzile sunt doar logate, nu se executa nimic.")]
    public static partial void DryRun(ILogger logger);

    [LoggerMessage(Level = LogLevel.Warning, Message = "DryRun este dezactivat: comenzile vor fi executate pe aceasta masina.")]
    public static partial void LiveMode(ILogger logger);
}
