using TailControl.Hub;
using TailControl.Shared.AspNetCore;

var contentRoot = AppContext.BaseDirectory;
var environment = Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT")
    ?? Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT")
    ?? "Production";
var isDevelopment = string.Equals(environment, "Development", StringComparison.OrdinalIgnoreCase);

// Configurarea specifica retelei (AllowedHosts, broadcast, certificat) sta in afara folderului
// aplicatiei, ca sa supravietuiasca actualizarilor. Setata de unitatea systemd.
var siteConfig = Environment.GetEnvironmentVariable("TAILCONTROL_HUB_CONFIG");

// Comenzile de administrare (authorize-client etc.) nu pornesc serverul.
if (AuthorizedClientsCli.IsCommand(args))
{
    var cliConfig = new ConfigurationBuilder()
        .SetBasePath(contentRoot)
        .AddJsonFile("appsettings.json", optional: true)
        .AddJsonFile($"appsettings.{environment}.json", optional: true);
    if (!string.IsNullOrEmpty(siteConfig))
    {
        cliConfig.AddJsonFile(siteConfig, optional: false);
    }

    return AuthorizedClientsCli.Run(args, HubOptions.Load(cliConfig.Build()).ToCliContext(isDevelopment), Console.Out, TimeProvider.System);
}

var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = args, ContentRootPath = contentRoot });
if (!string.IsNullOrEmpty(siteConfig))
{
    builder.Configuration.AddJsonFile(siteConfig, optional: false);
}

builder.Host.UseSystemd();

var options = HubOptions.Load(builder.Configuration);
TailscaleHosting.ValidateConfiguration(builder.Configuration, builder.Environment.IsDevelopment());

var listen = options.ToListenSettings();
var addresses = await TailscaleHosting.ResolveListenAddressesAsync(listen, builder.Environment.IsDevelopment());
builder.WebHost.ConfigureKestrel(k => TailscaleHosting.ConfigureKestrel(k, listen, addresses));
builder.Services.AddHubServices(options);

var app = builder.Build();
await app.InitializeDatabaseAsync();
app.UseHubPipeline();

var listenAddresses = string.Join(", ", addresses);
StartupLog.Started(app.Logger, listenAddresses, options.Port, listen.HasCertificate ? "HTTPS" : "HTTP, fara certificat");
if (options.ParsedWakeBroadcast is null)
{
    StartupLog.WakeDisabled(app.Logger);
}

app.LogAuthorizedClientsState();

await app.RunAsync();
return 0;

internal static partial class StartupLog
{
    [LoggerMessage(Level = LogLevel.Information, Message = "Hub pornit pe {Addresses}, port {Port} ({Scheme})")]
    public static partial void Started(ILogger logger, string addresses, int port, string scheme);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Hub:WakeBroadcastAddress nu e setat: pornirea dispozitivelor este dezactivata.")]
    public static partial void WakeDisabled(ILogger logger);
}
