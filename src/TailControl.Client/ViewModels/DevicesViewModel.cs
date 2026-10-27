using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TailControl.Client.Core;
using TailControl.Shared.Contracts;

namespace TailControl.Client.ViewModels;

public static class Routes
{
    public const string Devices = "//devices";
    public const string Settings = "//settings";
    public const string Device = "device";
    public const string DeviceEdit = "device-edit";
}

public sealed partial class DeviceItem(int id) : ObservableObject
{
    public int Id { get; } = id;

    [ObservableProperty]
    public partial string Name { get; set; } = "";

    [ObservableProperty]
    public partial string Host { get; set; } = "";

    [ObservableProperty]
    public partial bool IsOnline { get; set; }

    [ObservableProperty]
    public partial string StatusText { get; set; } = "";

    [ObservableProperty]
    public partial string KindText { get; set; } = "";

    public void Update(DeviceDto dto, DateTimeOffset now)
    {
        Name = dto.Name;
        Host = dto.Host;
        IsOnline = dto.Online;
        StatusText = DeviceActions.LastSeenText(dto, now);
        KindText = DeviceKindLabels.Label(dto.Kind);
    }
}

public static class DeviceKindLabels
{
    public static IReadOnlyList<KindOption> All { get; } =
    [
        new(DeviceKind.Desktop, "PC"),
        new(DeviceKind.Laptop, "Laptop"),
        new(DeviceKind.Other, "Altul"),
    ];

    public static string Label(DeviceKind kind) => All.FirstOrDefault(k => k.Kind == kind)?.Label ?? kind.ToString();
}

public sealed record KindOption(DeviceKind Kind, string Label)
{
    public override string ToString() => Label;
}

/// <summary>Lista dispozitivelor cu status, reimprospatata automat cat timp pagina e vizibila.</summary>
public sealed partial class DevicesViewModel(TailControlApi api, IClientSettingsStore settings, TimeProvider time) : ObservableObject
{
    private static readonly TimeSpan AutoRefresh = TimeSpan.FromSeconds(10);

    private IDispatcherTimer? _timer;
    private bool _loading;

    public ObservableCollection<DeviceItem> Devices { get; } = [];

    [ObservableProperty]
    public partial bool IsRefreshing { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    public partial string? ErrorMessage { get; set; }

    [ObservableProperty]
    public partial bool IsConfigured { get; set; }

    [ObservableProperty]
    public partial bool IsEmpty { get; set; }

    public bool HasError => !string.IsNullOrEmpty(ErrorMessage);

    public async Task OnAppearingAsync()
    {
        IsConfigured = settings.Load().IsConfigured;
        await LoadAsync();

        _timer ??= CreateTimer();
        _timer.Start();
    }

    public void OnDisappearing() => _timer?.Stop();

    [RelayCommand]
    private async Task RefreshAsync()
    {
        try
        {
            await LoadAsync();
        }
        finally
        {
            IsRefreshing = false;
        }
    }

    [RelayCommand]
    private static Task OpenAsync(DeviceItem item) => Shell.Current.GoToAsync($"{Routes.Device}?id={item.Id}");

    [RelayCommand]
    private static Task AddAsync() => Shell.Current.GoToAsync(Routes.DeviceEdit);

    [RelayCommand]
    private static Task OpenSettingsAsync() => Shell.Current.GoToAsync(Routes.Settings);

    private async Task LoadAsync()
    {
        if (_loading)
        {
            return;
        }

        _loading = true;
        try
        {
            var current = settings.Load();
            IsConfigured = current.IsConfigured;
            if (!IsConfigured)
            {
                Devices.Clear();
                IsEmpty = false;
                ErrorMessage = null;
                return;
            }

            var result = await api.GetDevicesAsync();
            if (!result.IsSuccess)
            {
                ErrorMessage = result.Error!.Message;
                return;
            }

            ErrorMessage = null;
            Sync(DeviceActions.VisibleTargets(result.Value!, current.ThisDeviceId), time.GetUtcNow());
            IsEmpty = Devices.Count == 0;
        }
        finally
        {
            _loading = false;
        }
    }

    // Actualizeaza elementele existente in loc sa refaca lista, ca reimprospatarea sa nu clipeasca.
    private void Sync(IReadOnlyList<DeviceDto> devices, DateTimeOffset now)
    {
        for (var i = Devices.Count - 1; i >= 0; i--)
        {
            if (devices.All(d => d.Id != Devices[i].Id))
            {
                Devices.RemoveAt(i);
            }
        }

        for (var i = 0; i < devices.Count; i++)
        {
            var existing = Devices.FirstOrDefault(d => d.Id == devices[i].Id);
            if (existing is null)
            {
                existing = new DeviceItem(devices[i].Id);
                Devices.Insert(i, existing);
            }
            else if (Devices.IndexOf(existing) != i)
            {
                Devices.Move(Devices.IndexOf(existing), i);
            }

            existing.Update(devices[i], now);
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
