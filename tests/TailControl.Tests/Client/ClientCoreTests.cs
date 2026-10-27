using TailControl.Client.Core;
using TailControl.Shared.Commands;
using TailControl.Shared.Contracts;

namespace TailControl.Tests.Client;

public sealed class ClientCoreTests
{
    private static readonly DateTimeOffset Now = TestSupport.Start;

    [Theory]
    [InlineData("pihole.tail1234.ts.net", "https://pihole.tail1234.ts.net:8443/")]
    [InlineData("  PiHole.Tail1234.TS.NET ", "https://pihole.tail1234.ts.net:8443/")]
    [InlineData("https://pihole.tail1234.ts.net", "https://pihole.tail1234.ts.net:8443/")]
    [InlineData("https://pihole.tail1234.ts.net:9443", "https://pihole.tail1234.ts.net:9443/")]
    [InlineData("http://pihole.tail1234.ts.net:8443/", "http://pihole.tail1234.ts.net:8443/")]
    public void TryParseHubUrl_AcceptsMagicDnsForms(string input, string expected)
    {
        Assert.True(ClientSettings.TryParseHubUrl(input, out var uri, out _));
        Assert.Equal(expected, uri.ToString());
    }

    [Theory]
    [InlineData("")]
    [InlineData("192.168.1.50")]
    [InlineData("https://100.64.1.2:8443")]
    [InlineData("pihole")]
    [InlineData("pihole.local")]
    [InlineData("ftp://pihole.tail1234.ts.net")]
    [InlineData("https://pihole.tail1234.ts.net:8443/v1/devices")]
    [InlineData("https://user@pihole.tail1234.ts.net:8443")]
    public void TryParseHubUrl_RejectsOthers(string input)
    {
        Assert.False(ClientSettings.TryParseHubUrl(input, out _, out var error));
        Assert.False(string.IsNullOrEmpty(error));
    }

    [Fact]
    public void Evaluate_OfflineWithMac_CanOnlyWake()
    {
        var state = DeviceActions.Evaluate(Device(online: false, mac: "AA:BB:CC:00:00:01"), null, Now);

        Assert.Equal(new DeviceActionState(true, false, false, false, false, null), state);
    }

    [Fact]
    public void Evaluate_OfflineWithoutMac_ExplainsWhy()
    {
        var state = DeviceActions.Evaluate(Device(online: false), null, Now);

        Assert.False(state.CanWake);
        Assert.Contains("MAC", state.WakeHint, StringComparison.Ordinal);
    }

    [Fact]
    public void Evaluate_Online_AllAgentActionsButWake()
    {
        var state = DeviceActions.Evaluate(Device(online: true, mac: "AA:BB:CC:00:00:01"), Status(null), Now);

        Assert.Equal(new DeviceActionState(false, true, false, true, true, null), state);
    }

    [Fact]
    public void Evaluate_ShutdownPending_OffersCancelInsteadOfShutdown()
    {
        var state = DeviceActions.Evaluate(Device(online: true), Status(Now.AddSeconds(5)), Now);

        Assert.True(state.CanCancelShutdown);
        Assert.False(state.CanShutdown);
        Assert.False(state.CanSleep);
        Assert.True(state.CanLock);
    }

    [Fact]
    public void Evaluate_ShutdownTimePassed_NoCancel()
    {
        var state = DeviceActions.Evaluate(Device(online: true), Status(Now.AddSeconds(-1)), Now);

        Assert.False(state.CanCancelShutdown);
        Assert.True(state.CanShutdown);
    }

    [Theory]
    [InlineData(AgentCommand.Shutdown, true)]
    [InlineData(AgentCommand.Sleep, true)]
    [InlineData(AgentCommand.Lock, false)]
    [InlineData(AgentCommand.CancelShutdown, false)]
    public void RequiresUserVerification_OnlyForPowerOff(AgentCommand command, bool expected)
    {
        Assert.Equal(expected, DeviceActions.RequiresUserVerification(command));
    }

    [Fact]
    public void VisibleTargets_ExcludesThisDevice_OnlineFirst()
    {
        var devices = new[]
        {
            Device(online: false, id: 1, name: "Laptop"),
            Device(online: true, id: 2, name: "PC"),
            Device(online: true, id: 3, name: "Acest PC"),
        };

        var visible = DeviceActions.VisibleTargets(devices, thisDeviceId: 3);

        Assert.Equal(["PC", "Laptop"], visible.Select(d => d.Name));
    }

    [Theory]
    [InlineData(-30, "Offline, vazut acum cateva secunde")]
    [InlineData(-300, "Offline, vazut acum 5 min")]
    [InlineData(-7200, "Offline, vazut acum 2 h")]
    [InlineData(-259200, "Offline, vazut acum 3 zile")]
    public void LastSeenText_IsHumanReadable(int secondsAgo, string expected)
    {
        var device = Device(online: false) with { LastSeenUtc = Now.AddSeconds(secondsAgo) };

        Assert.Equal(expected, DeviceActions.LastSeenText(device, Now));
    }

    [Theory]
    [InlineData("Pixel 8 \"Pro\"; rm -rf /", "Pixel 8 Pro rm -rf")]
    [InlineData("$(whoami)", "whoami")]
    [InlineData("", "Client")]
    [InlineData("Telefon Tavi", "Telefon Tavi")]
    public void SafeLabel_RemovesShellCharacters(string input, string expected)
    {
        Assert.Equal(expected, AuthorizationInstructions.SafeLabel(input));
    }

    [Fact]
    public void AuthorizationCommands_ContainKeyAndQuotedLabel()
    {
        Assert.Equal(
            "sudo /opt/tailcontrol-hub/TailControl.Hub authorize-client --key KEY --label \"Telefon\" --admin",
            AuthorizationInstructions.ForHub("KEY", "Telefon"));
        Assert.Equal(
            "TailControl.Agent.exe authorize-client --key KEY --label \"Telefon\"",
            AuthorizationInstructions.ForAgent("KEY", "Telefon"));
    }

    [Fact]
    public async Task IdentityProvider_PersistsKey()
    {
        var store = new InMemoryKeyStore();
        using var first = new ClientIdentityProvider(store);
        var id = (await first.GetAsync()).ClientId;

        using var second = new ClientIdentityProvider(store);

        Assert.Equal(id, (await second.GetAsync()).ClientId);
        Assert.False(second.WasRegenerated);
    }

    [Fact]
    public async Task IdentityProvider_CorruptKey_IsReplaced_AndFlagged()
    {
        var store = new InMemoryKeyStore();
        await store.SetAsync("nu e o cheie");
        using var provider = new ClientIdentityProvider(store);

        var identity = await provider.GetAsync();

        Assert.True(provider.WasRegenerated);
        Assert.NotEqual("nu e o cheie", await store.GetAsync());
        Assert.True(SignatureProtocolIsValid(identity.ClientId));
    }

    private static bool SignatureProtocolIsValid(string id) => TailControl.Shared.Security.SignatureProtocol.IsValidClientId(id);

    private static DeviceDto Device(bool online, string? mac = null, int id = 1, string name = "PC") =>
        new(id, name, mac, $"{name.ToLowerInvariant().Replace(' ', '-')}.example.ts.net", 8444, DeviceKind.Desktop, online, null, Now, Now, Now);

    private static AgentStatusResponse Status(DateTimeOffset? shutdownAt) =>
        new("0.1.0", "PC", false, shutdownAt, 1);
}
