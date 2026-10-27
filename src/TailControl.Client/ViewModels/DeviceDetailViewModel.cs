using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TailControl.Client.Core;
using TailControl.Client.Services;
using TailControl.Shared.Commands;
using TailControl.Shared.Contracts;

namespace TailControl.Client.ViewModels;

/// <summary>
/// Ecranul unui dispozitiv: status (de la Hub si de la agent) si comenzile. Pornirea merge prin
/// Hub; oprirea, blocarea si sleep-ul merg direct la agent.
/// </summary>
public sealed partial class DeviceDetailViewModel(
    TailControlApi api,
    IClientSettingsStore settings,
    IUserVerifier verifier,
    IDialogs dialogs,
    TimeProvider time) : ObservableObject, IQueryAttributable
{
    private static readonly TimeSpan AutoRefresh = TimeSpan.FromSeconds(5);

    private int _id;
    private IDispatcherTimer? _timer;
    private bool _loading;
    private DeviceActionState _actions = new(false, false, false, false, false, null);

    [ObservableProperty]
    public partial DeviceDto? Device { get; set; }

    [ObservableProperty]
    public partial string Title { get; set; } = "Dispozitiv";

    [ObservableProperty]
    public partial bool IsOnline { get; set; }

    [ObservableProperty]
    public partial string StatusText { get; set; } = "";

    [ObservableProperty]
    public partial string? AgentInfo { get; set; }

    [ObservableProperty]
    public partial bool IsDryRun { get; set; }

    [ObservableProperty]
    public partial string? ShutdownCountdown { get; set; }

    [ObservableProperty]
    public partial string? WakeHint { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasMessage))]
    public partial string? Message { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    public partial string? ErrorMessage { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanWake), nameof(CanShutdown), nameof(CanCancelShutdown), nameof(CanLock), nameof(CanSleep))]
    public partial bool IsBusy { get; set; }

    public bool HasMessage => !string.IsNullOrEmpty(Message);

    public bool HasError => !string.IsNullOrEmpty(ErrorMessage);

    public bool CanWake => !IsBusy && _actions.CanWake;

    public bool CanShutdown => !IsBusy && _actions.CanShutdown;

    public bool CanCancelShutdown => !IsBusy && _actions.CanCancelShutdown;

    public bool CanLock => !IsBusy && _actions.CanLock;

    public bool CanSleep => !IsBusy && _actions.CanSleep;

    public void ApplyQueryAttributes(IDictionary<string, object> query)
    {
        if (query.TryGetValue("id", out var value) && int.TryParse(value?.ToString(), out var id))
        {
            _id = id;
        }
    }

    public async Task OnAppearingAsync()
    {
        await LoadAsync();
        _timer ??= CreateTimer();
        _timer.Start();
    }

    public void OnDisappearing() => _timer?.Stop();

    [RelayCommand]
    private async Task WakeAsync()
    {
        if (Device is not { } device
            || !await dialogs.ConfirmAsync("Porneste", $"Trimit pachetul de pornire catre {device.Name}?", "Porneste"))
        {
            return;
        }

        await RunAsync(async () =>
        {
            var result = await api.WakeAsync(device.Id);
            ShowResult(result.IsSuccess ? result.Value!.Message : null, result.Error);
        });
    }

    [RelayCommand]
    private Task ShutdownAsync() => SendAsync(AgentCommand.Shutdown, "Opreste");

    [RelayCommand]
    private Task CancelShutdownAsync() => SendAsync(AgentCommand.CancelShutdown, "Anuleaza oprirea", confirm: false);

    [RelayCommand]
    private Task LockAsync() => SendAsync(AgentCommand.Lock, "Blocheaza");

    [RelayCommand]
    private Task SleepAsync() => SendAsync(AgentCommand.Sleep, "Adoarme");

    [RelayCommand]
    private Task EditAsync() => Shell.Current.GoToAsync($"{Routes.DeviceEdit}?id={_id}");

    [RelayCommand]
    private async Task DeleteAsync()
    {
        if (Device is not { } device
            || !await dialogs.ConfirmAsync("Sterge", $"Stergi {device.Name} din inventar? Agentul de pe dispozitiv nu este afectat.", "Sterge"))
        {
            return;
        }

        var result = await api.DeleteDeviceAsync(device.Id);
        if (result.IsSuccess)
        {
            await Shell.Current.GoToAsync("..");
        }
        else
        {
            ErrorMessage = result.Error!.Message;
        }
    }

    private async Task SendAsync(AgentCommand command, string action, bool confirm = true)
    {
        if (Device is not { } device)
        {
            return;
        }

        if (confirm && !await dialogs.ConfirmAsync(action, DeviceActions.ConfirmationText(command, device.Name), action))
        {
            return;
        }

        if (settings.Load().RequireUserVerification && DeviceActions.RequiresUserVerification(command))
        {
            var verified = await verifier.VerifyAsync($"{action}: {device.Name}");
            if (verified == UserVerificationResult.Denied)
            {
                return;
            }

            if (verified == UserVerificationResult.NotAvailable)
            {
                await dialogs.AlertAsync("Fara confirmare suplimentara",
                    "Pe acest dispozitiv nu e configurata amprenta, PIN-ul sau Windows Hello, asa ca pasul de confirmare a identitatii a fost sarit.");
            }
        }

        await RunAsync(async () =>
        {
            var result = await api.SendCommandAsync(device, command);
            ShowResult(result.IsSuccess ? result.Value!.Message : null, result.Error);
        });
    }

    private async Task RunAsync(Func<Task> action)
    {
        IsBusy = true;
        Message = null;
        ErrorMessage = null;
        try
        {
            await action();
        }
        finally
        {
            IsBusy = false;
        }

        await LoadAsync();
    }

    private void ShowResult(string? message, ApiError? error)
    {
        Message = message;
        ErrorMessage = error?.Message;
    }

    private async Task LoadAsync()
    {
        if (_loading || _id == 0)
        {
            return;
        }

        _loading = true;
        try
        {
            var result = await api.GetDeviceAsync(_id);
            if (!result.IsSuccess)
            {
                ErrorMessage ??= result.Error!.Message;
                return;
            }

            var device = result.Value!;
            AgentStatusResponse? status = null;
            AgentInfo = null;
            if (device.Online)
            {
                var agent = await api.GetAgentStatusAsync(device);
                status = agent.Value;
                AgentInfo = agent.IsSuccess
                    ? $"Agent {status!.AgentVersion} pe {status.MachineName}" + (status.LockHelperSessions == 0 ? " (niciun utilizator logat)" : "")
                    : agent.Error!.Message;
            }

            var now = time.GetUtcNow();
            Device = device;
            Title = device.Name;
            IsOnline = device.Online;
            StatusText = DeviceActions.LastSeenText(device, now);
            IsDryRun = status?.DryRun == true;
            ShutdownCountdown = status?.ShutdownScheduledForUtc is { } at && at > now
                ? $"Oprire in {Math.Ceiling((at - now).TotalSeconds):0} s"
                : null;

            _actions = DeviceActions.Evaluate(device, status, now);
            WakeHint = _actions.WakeHint;
            OnPropertyChanged(nameof(CanWake));
            OnPropertyChanged(nameof(CanShutdown));
            OnPropertyChanged(nameof(CanCancelShutdown));
            OnPropertyChanged(nameof(CanLock));
            OnPropertyChanged(nameof(CanSleep));
        }
        finally
        {
            _loading = false;
        }
    }

    private IDispatcherTimer CreateTimer()
    {
        var timer = Application.Current!.Dispatcher.CreateTimer();
        timer.Interval = AutoRefresh;
        timer.Tick += async (_, _) => await LoadAsync();
        return timer;
    }
}
