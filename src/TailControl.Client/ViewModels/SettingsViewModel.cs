using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using QRCoder;
using TailControl.Client.Core;
using TailControl.Client.Services;
using TailControl.Shared.Contracts;

namespace TailControl.Client.ViewModels;

public sealed record ThisDeviceOption(int? Id, string Label)
{
    public override string ToString() => Label;
}

/// <summary>Adresa Hub-ului, dispozitivul curent, securitate si identitatea clientului (pairing).</summary>
public sealed partial class SettingsViewModel(
    TailControlApi api,
    IClientSettingsStore settings,
    ClientIdentityProvider identities,
    IDialogs dialogs) : ObservableObject
{
    private static readonly ThisDeviceOption None = new(null, "(niciunul)");

    private bool _loading;

    [ObservableProperty]
    public partial string HubUrlText { get; set; } = "";

    [ObservableProperty]
    public partial string? HubStatus { get; set; }

    [ObservableProperty]
    public partial bool HubOk { get; set; }

    public ObservableCollection<ThisDeviceOption> DeviceOptions { get; } = [None];

    [ObservableProperty]
    public partial ThisDeviceOption? SelectedThisDevice { get; set; }

    [ObservableProperty]
    public partial bool AgentsUseHttps { get; set; } = true;

    [ObservableProperty]
    public partial bool RequireUserVerification { get; set; } = true;

    [ObservableProperty]
    public partial string ClientId { get; set; } = "";

    [ObservableProperty]
    public partial string PublicKey { get; set; } = "";

    [ObservableProperty]
    public partial ImageSource? QrCode { get; set; }

    [ObservableProperty]
    public partial string? IdentityWarning { get; set; }

    public static string DeviceLabel => AuthorizationInstructions.SafeLabel(DeviceInfo.Current.Name);

    public async Task OnAppearingAsync()
    {
        _loading = true;
        try
        {
            var current = settings.Load();
            HubUrlText = current.HubUrl?.ToString().TrimEnd('/') ?? "";
            AgentsUseHttps = current.AgentsUseHttps;
            RequireUserVerification = current.RequireUserVerification;
            SelectedThisDevice ??= None;

            await LoadIdentityAsync();
            await LoadDevicesAsync(current);
        }
        finally
        {
            _loading = false;
        }
    }

    [RelayCommand]
    private async Task SaveHubAsync()
    {
        if (!ClientSettings.TryParseHubUrl(HubUrlText, out var uri, out var error))
        {
            HubOk = false;
            HubStatus = error;
            return;
        }

        settings.Save(settings.Load() with { HubUrl = uri });
        HubUrlText = uri.ToString().TrimEnd('/');
        await LoadDevicesAsync(settings.Load());
    }

    [RelayCommand]
    private async Task CopyAsync(string what)
    {
        var text = what switch
        {
            "key" => PublicKey,
            "hub" => AuthorizationInstructions.ForHub(PublicKey, DeviceLabel),
            "agent" => AuthorizationInstructions.ForAgent(PublicKey, DeviceLabel),
            _ => "",
        };

        await Clipboard.Default.SetTextAsync(text);
        await dialogs.ToastAsync("Copiat in clipboard.");
    }

    [RelayCommand]
    private Task ShareAsync() => Share.Default.RequestAsync(new ShareTextRequest
    {
        Title = "Autorizare TailControl",
        Text = $"Cheia publica TailControl ({DeviceLabel}):\n{PublicKey}\n\nPe Hub:\n{AuthorizationInstructions.ForHub(PublicKey, DeviceLabel)}\n\nPe agent (ca administrator):\n{AuthorizationInstructions.ForAgent(PublicKey, DeviceLabel)}",
    });

    [RelayCommand]
    private async Task RegenerateKeyAsync()
    {
        if (!await dialogs.ConfirmAsync("Cheie noua",
            "Generez o pereche de chei noua? Dispozitivul va trebui autorizat din nou pe Hub si pe fiecare agent, iar cheia veche trebuie revocata acolo.",
            "Genereaza"))
        {
            return;
        }

        await identities.RegenerateAsync();
        await LoadIdentityAsync();
    }

    partial void OnSelectedThisDeviceChanged(ThisDeviceOption? value)
    {
        if (!_loading && value is not null)
        {
            settings.Save(settings.Load() with { ThisDeviceId = value.Id });
        }
    }

    partial void OnAgentsUseHttpsChanged(bool value)
    {
        if (!_loading)
        {
            settings.Save(settings.Load() with { AgentsUseHttps = value });
        }
    }

    partial void OnRequireUserVerificationChanged(bool value)
    {
        if (!_loading)
        {
            settings.Save(settings.Load() with { RequireUserVerification = value });
        }
    }

    private async Task LoadIdentityAsync()
    {
        var identity = await identities.GetAsync();
        ClientId = identity.ClientId;
        PublicKey = identity.PublicKey;
        QrCode = CreateQr(identity.PublicKey);
        IdentityWarning = identities.WasRegenerated
            ? "Cheia salvata nu a putut fi citita si a fost generata una noua. Autorizeaza din nou acest dispozitiv."
            : null;
    }

    private async Task LoadDevicesAsync(ClientSettings current)
    {
        if (!current.IsConfigured)
        {
            HubOk = false;
            HubStatus = "Hub neconfigurat.";
            return;
        }

        HubStatus = "Se verifica legatura cu Hub-ul...";
        var result = await api.GetDevicesAsync();
        HubOk = result.IsSuccess;
        if (!result.IsSuccess)
        {
            HubStatus = result.Error!.Message;
            return;
        }

        HubStatus = $"Conectat. {result.Value!.Count} dispozitive in inventar.";

        var wasLoading = _loading;
        _loading = true;
        DeviceOptions.Clear();
        DeviceOptions.Add(None);
        foreach (var d in result.Value.OrderBy(d => d.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            DeviceOptions.Add(new ThisDeviceOption(d.Id, d.Name));
        }

        var selected = DeviceOptions.FirstOrDefault(o => o.Id == current.ThisDeviceId);
        if (selected is null || selected == None)
        {
            // Pe Windows putem ghici: hostul MagicDNS incepe de obicei cu numele calculatorului.
            selected = SuggestThisDevice(result.Value) ?? None;
        }

        SelectedThisDevice = selected;
        _loading = wasLoading;
        if (selected != None && current.ThisDeviceId != selected.Id)
        {
            settings.Save(settings.Load() with { ThisDeviceId = selected.Id });
        }
    }

    private ThisDeviceOption? SuggestThisDevice(IEnumerable<DeviceDto> devices)
    {
        if (DeviceInfo.Current.Platform != DevicePlatform.WinUI)
        {
            return null;
        }

        var machine = Environment.MachineName.ToLowerInvariant();
        var match = devices.FirstOrDefault(d => d.Host.Split('.')[0] == machine);
        return match is null ? null : DeviceOptions.First(o => o.Id == match.Id);
    }

    private static ImageSource CreateQr(string text)
    {
        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(text, QRCodeGenerator.ECCLevel.M);
        var png = new PngByteQRCode(data).GetGraphic(8);
        return ImageSource.FromStream(() => new MemoryStream(png));
    }
}
