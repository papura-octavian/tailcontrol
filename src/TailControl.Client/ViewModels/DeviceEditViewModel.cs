using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TailControl.Client.Core;
using TailControl.Shared.Contracts;
using TailControl.Shared.Validation;

namespace TailControl.Client.ViewModels;

/// <summary>Adaugare / editare dispozitiv. Validarea e aceeasi ca pe Hub (din TailControl.Shared).</summary>
public sealed partial class DeviceEditViewModel(TailControlApi api) : ObservableObject, IQueryAttributable
{
    private int? _id;

#pragma warning disable CA1822 // folosit din binding-ul XAML compilat
    public IReadOnlyList<KindOption> Kinds => DeviceKindLabels.All;
#pragma warning restore CA1822

    [ObservableProperty]
    public partial string Title { get; set; } = "Dispozitiv nou";

    [ObservableProperty]
    public partial string Name { get; set; } = "";

    [ObservableProperty]
    public partial string Host { get; set; } = "";

    [ObservableProperty]
    public partial string MacAddress { get; set; } = "";

    [ObservableProperty]
    public partial string AgentPort { get; set; } = AgentRoutes.DefaultPort.ToString(CultureInfo.InvariantCulture);

    [ObservableProperty]
    public partial KindOption SelectedKind { get; set; } = DeviceKindLabels.All[0];

    [ObservableProperty]
    public partial string? NameError { get; set; }

    [ObservableProperty]
    public partial string? HostError { get; set; }

    [ObservableProperty]
    public partial string? MacError { get; set; }

    [ObservableProperty]
    public partial string? PortError { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    public partial string? ErrorMessage { get; set; }

    [ObservableProperty]
    public partial bool IsBusy { get; set; }

    public bool HasError => !string.IsNullOrEmpty(ErrorMessage);

    public async void ApplyQueryAttributes(IDictionary<string, object> query)
    {
        if (!query.TryGetValue("id", out var value) || !int.TryParse(value?.ToString(), out var id))
        {
            return;
        }

        _id = id;
        Title = "Editeaza dispozitivul";
        var result = await api.GetDeviceAsync(id);
        if (!result.IsSuccess)
        {
            ErrorMessage = result.Error!.Message;
            return;
        }

        var d = result.Value!;
        Name = d.Name;
        Host = d.Host;
        MacAddress = d.MacAddress ?? "";
        AgentPort = d.AgentPort.ToString(CultureInfo.InvariantCulture);
        SelectedKind = DeviceKindLabels.All.First(k => k.Kind == d.Kind);
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        ErrorMessage = null;
        // Gol = portul implicit al agentului; orice text care nu e numar = invalid.
        int? port = string.IsNullOrWhiteSpace(AgentPort) ? null
            : int.TryParse(AgentPort.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var p) ? p : -1;
        var request = new DeviceRequest(Name, string.IsNullOrWhiteSpace(MacAddress) ? null : MacAddress, Host, port, SelectedKind.Kind);

        // Validare locala pentru mesaje imediate; Hub-ul valideaza oricum din nou.
        var errors = DeviceValidation.Validate(request, [DeviceValidation.DefaultHostSuffix], out var normalized);
        ShowErrors(errors);
        if (errors.Count > 0)
        {
            return;
        }

        IsBusy = true;
        try
        {
            var result = _id is { } id
                ? await api.UpdateDeviceAsync(id, normalized)
                : await api.CreateDeviceAsync(normalized);

            if (result.IsSuccess)
            {
                await Shell.Current.GoToAsync("..");
                return;
            }

            ErrorMessage = result.Error!.Message;
            if (result.Error.FieldErrors is { } fieldErrors)
            {
                ShowErrors(fieldErrors);
            }
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void ShowErrors(IReadOnlyDictionary<string, string[]> errors)
    {
        NameError = First(errors, "name");
        HostError = First(errors, "host");
        MacError = First(errors, "macAddress");
        PortError = First(errors, "agentPort");
    }

    private static string? First(IReadOnlyDictionary<string, string[]> errors, string key) =>
        errors.TryGetValue(key, out var messages) ? messages.FirstOrDefault() : null;
}
