using Microsoft.Extensions.Logging;
using TailControl.Client.Core;
using TailControl.Client.Services;
using TailControl.Client.ViewModels;
using TailControl.Client.Views;

namespace TailControl.Client;

public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
        var builder = MauiApp.CreateBuilder();
        builder
            .UseMauiApp<App>()
            .ConfigureFonts(fonts =>
            {
                fonts.AddFont("OpenSans-Regular.ttf", "OpenSansRegular");
                fonts.AddFont("OpenSans-Semibold.ttf", "OpenSansSemibold");
            });

#if DEBUG
        builder.Logging.AddDebug();
#endif

        var services = builder.Services;
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<IClientSettingsStore, PreferencesSettingsStore>();
        services.AddSingleton<IKeyStore, SecureKeyStore>();
        services.AddSingleton<ClientIdentityProvider>();
        services.AddSingleton(sp => new TailControlApi(
            sp.GetRequiredService<ClientIdentityProvider>(),
            sp.GetRequiredService<IClientSettingsStore>(),
            () => new SocketsHttpHandler
            {
                ConnectTimeout = TimeSpan.FromSeconds(5),
                PooledConnectionLifetime = TimeSpan.FromMinutes(2),
            },
            sp.GetRequiredService<TimeProvider>()));
        services.AddSingleton<IDialogs, ShellDialogs>();

#if ANDROID
        services.AddSingleton<IUserVerifier, AndroidUserVerifier>();
#elif WINDOWS
        services.AddSingleton<IUserVerifier, WindowsUserVerifier>();
#else
        services.AddSingleton<IUserVerifier, UnsupportedUserVerifier>();
#endif

        services.AddSingleton<AppShell>();
        services.AddTransient<DevicesPage>().AddTransient<DevicesViewModel>();
        services.AddTransient<DeviceDetailPage>().AddTransient<DeviceDetailViewModel>();
        services.AddTransient<DeviceEditPage>().AddTransient<DeviceEditViewModel>();
        services.AddTransient<SettingsPage>().AddTransient<SettingsViewModel>();

        return builder.Build();
    }
}
