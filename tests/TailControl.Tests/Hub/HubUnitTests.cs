using System.Net;
using System.Net.Sockets;
using TailControl.Hub;
using TailControl.Hub.Status;
using TailControl.Hub.Wake;
using TailControl.Shared.AspNetCore;
using TailControl.Shared.Contracts;
using TailControl.Shared.Security;
using TailControl.Shared.Validation;

namespace TailControl.Tests.Hub;

public sealed class HubUnitTests
{
    private static readonly string[] TsNet = [DeviceValidation.DefaultHostSuffix];

    [Theory]
    [InlineData("CC:28:AA:00:00:01", "CC:28:AA:00:00:01")]
    [InlineData("cc:28:aa:00:00:01", "CC:28:AA:00:00:01")]
    [InlineData("cc-28-aa-00-00-01", "CC:28:AA:00:00:01")]
    [InlineData("cc28aa000001", "CC:28:AA:00:00:01")]
    [InlineData(" CC:28:AA:00:00:01 ", "CC:28:AA:00:00:01")]
    public void TryNormalizeMac_AcceptsCommonFormats(string input, string expected)
    {
        Assert.True(DeviceValidation.TryNormalizeMac(input, out var normalized));
        Assert.Equal(expected, normalized);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("CC:28:AA:00:00")]
    [InlineData("CC:28:AA:00:00:01:02")]
    [InlineData("CC:28-AA:00:00:01")]
    [InlineData("GG:28:AA:00:00:01")]
    [InlineData("CC 28 AA 00 00 01")]
    [InlineData("01:00:5E:00:00:01")]
    [InlineData("FF:FF:FF:FF:FF:FF")]
    [InlineData("00:00:00:00:00:00")]
    public void TryNormalizeMac_RejectsInvalid(string? input)
    {
        Assert.False(DeviceValidation.TryNormalizeMac(input, out _));
    }

    [Theory]
    [InlineData("pc.tailnet-1234.ts.net", "pc.tailnet-1234.ts.net")]
    [InlineData("PC.Tailnet-1234.TS.NET.", "pc.tailnet-1234.ts.net")]
    [InlineData("laptop-2.tail0.ts.net", "laptop-2.tail0.ts.net")]
    public void TryNormalizeHost_AcceptsMagicDns(string input, string expected)
    {
        Assert.True(DeviceValidation.TryNormalizeHost(input, TsNet, out var normalized));
        Assert.Equal(expected, normalized);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("pc")]
    [InlineData("ts.net")]
    [InlineData("100.64.0.1")]
    [InlineData("fd7a:115c:a1e0::1")]
    [InlineData("pc.local")]
    [InlineData("pc.ts.net.evil.com")]
    [InlineData("evilts.net")]
    [InlineData("-pc.tailnet.ts.net")]
    [InlineData("pc..tailnet.ts.net")]
    [InlineData("pc tailnet.ts.net")]
    [InlineData("pc.tailnet.ts.net/../x")]
    public void TryNormalizeHost_RejectsEverythingElse(string? input)
    {
        Assert.False(DeviceValidation.TryNormalizeHost(input, TsNet, out _));
    }

    [Fact]
    public void MagicPacket_Has6xFF_Then16xMac()
    {
        byte[] mac = [0xCC, 0x28, 0xAA, 0x00, 0x00, 0x01];

        var packet = MagicPacket.Build(mac);

        Assert.Equal(102, packet.Length);
        Assert.All(packet[..6], b => Assert.Equal(0xFF, b));
        for (var i = 0; i < 16; i++)
        {
            Assert.Equal(mac, packet[(6 + (i * 6))..(12 + (i * 6))]);
        }
    }

    [Fact]
    public void MagicPacket_WrongMacLength_Throws()
    {
        Assert.Throws<ArgumentException>(() => MagicPacket.Build(new byte[5]));
    }

    [Fact]
    public async Task UdpWakeOnLanSender_SendsMagicPacket()
    {
        using var listener = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var port = ((IPEndPoint)listener.Client.LocalEndPoint!).Port;
        var sender = new UdpWakeOnLanSender(new HubOptions { WakeBroadcastAddress = "127.0.0.1", WakePort = port });

        await sender.SendAsync([0xCC, 0x28, 0xAA, 0x00, 0x00, 0x01], CancellationToken.None);
        var received = await listener.ReceiveAsync().WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(MagicPacket.Build([0xCC, 0x28, 0xAA, 0x00, 0x00, 0x01]), received.Buffer);
    }

    [Theory]
    [InlineData("192.168.1.255", true)]
    [InlineData("10.0.0.255", true)]
    [InlineData("172.16.5.255", true)]
    [InlineData("255.255.255.255", true)]
    [InlineData("192.168.1.10", false)]
    [InlineData("8.8.8.255", false)]
    [InlineData("100.64.0.255", false)]
    [InlineData("172.32.0.255", false)]
    [InlineData("ff02::1", false)]
    [InlineData("nu e ip", false)]
    public void WakeBroadcast_OnlyPrivateLanBroadcast(string address, bool allowed)
    {
        Assert.Equal(allowed, HubOptions.IsAllowedBroadcast(address));
    }

    [Theory]
    [InlineData(4)]
    [InlineData(301)]
    public void HubOptions_InvalidStatusInterval_Throws(int seconds)
    {
        Assert.Throws<InvalidOperationException>(() => new HubOptions { StatusIntervalSeconds = seconds }.Validate());
    }

    [Fact]
    public void HubOptions_PublicBroadcast_Throws()
    {
        Assert.Throws<InvalidOperationException>(() => new HubOptions { WakeBroadcastAddress = "8.8.8.255" }.Validate());
    }

    [Fact]
    public async Task TcpDeviceProbe_DetectsOpenAndClosedPorts()
    {
        var probe = new TcpDeviceProbe();
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        Assert.True(await probe.IsReachableAsync("127.0.0.1", port, TimeSpan.FromSeconds(10), CancellationToken.None));

        listener.Stop();
        Assert.False(await probe.IsReachableAsync("127.0.0.1", port, TimeSpan.FromSeconds(2), CancellationToken.None));
    }

    [Fact]
    public void HubCli_AdminFlag_MarksClientAdmin()
    {
        var directory = TestSupport.NewTempPath("hub-cli");
        try
        {
            using var phone = ClientIdentity.Create();
            var options = new HubOptions { ConfigDirectory = directory };

            var exit = AuthorizedClientsCli.Run(
                ["authorize-client", "--key", phone.PublicKey, "--label", "Telefon", "--admin"],
                options.ToCliContext(isDevelopment: true), new StringWriter(), TimeProvider.System);

            Assert.Equal(0, exit);
            Assert.True(AuthorizedClientsFile.LoadOrEmpty(options.AuthorizedClientsPath).Find(phone.ClientId)!.Client.IsAdmin);
        }
        finally
        {
            TestSupport.DeleteQuietly(directory);
        }
    }

    [Fact]
    public void AgentCli_RejectsAdminFlag()
    {
        using var phone = ClientIdentity.Create();
        var options = new TailControl.Agent.AgentOptions { DataDirectory = TestSupport.NewTempPath("agent-cli") };

        var exit = AuthorizedClientsCli.Run(
            ["authorize-client", "--key", phone.PublicKey, "--label", "Telefon", "--admin"],
            options.ToCliContext(isDevelopment: true), new StringWriter(), TimeProvider.System);

        Assert.Equal(2, exit);
        Assert.False(File.Exists(options.AuthorizedClientsPath));
    }

    [Fact]
    public void DeviceValidation_DefaultsAgentPort()
    {
        var errors = DeviceValidation.Validate(new DeviceRequest("PC", null, "pc.example.ts.net", null, DeviceKind.Desktop), TsNet, out var normalized);

        Assert.Empty(errors);
        Assert.Equal(AgentRoutes.DefaultPort, normalized.AgentPort);
    }
}
