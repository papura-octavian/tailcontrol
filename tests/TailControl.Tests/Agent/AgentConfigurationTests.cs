using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Configuration;
using TailControl.Agent;
using TailControl.Shared.AspNetCore;
using TailControl.Shared.Security;

namespace TailControl.Tests.Agent;

public sealed class AgentConfigurationTests : IDisposable
{
    private readonly string _directory = TestSupport.NewTempPath("agent-config");

    public AgentConfigurationTests() => Directory.CreateDirectory(_directory);

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    [Theory]
    [InlineData("100.64.0.1", true)]
    [InlineData("100.101.102.103", true)]
    [InlineData("100.127.255.254", true)]
    [InlineData("100.63.255.255", false)]
    [InlineData("100.128.0.1", false)]
    [InlineData("192.168.0.10", false)]
    [InlineData("10.0.0.1", false)]
    [InlineData("0.0.0.0", false)]
    [InlineData("127.0.0.1", false)]
    [InlineData("fd7a:115c:a1e0::1", true)]
    [InlineData("fd7a:115c:a1e1::1", false)]
    [InlineData("::", false)]
    [InlineData("::ffff:100.64.0.1", true)]
    public void IsTailscaleAddress_MatchesOnlyTailscaleRanges(string address, bool expected)
    {
        Assert.Equal(expected, TailscaleNetwork.IsTailscaleAddress(IPAddress.Parse(address)));
    }

    [Theory]
    [InlineData(System.Net.NetworkInformation.OperationalStatus.Up, true)]
    [InlineData(System.Net.NetworkInformation.OperationalStatus.Unknown, true)] // tailscale0 pe Linux
    [InlineData(System.Net.NetworkInformation.OperationalStatus.Down, false)]
    [InlineData(System.Net.NetworkInformation.OperationalStatus.NotPresent, false)]
    public void IsUsable_AcceptsUnknownStateOfTunInterfaces(System.Net.NetworkInformation.OperationalStatus status, bool expected)
    {
        Assert.Equal(expected, TailscaleNetwork.IsUsable(status));
    }

    [Theory]
    [InlineData("0.0.0.0")]
    [InlineData("::")]
    [InlineData("192.168.0.10")]
    [InlineData("127.0.0.1")]
    public void ValidateListenAddresses_RejectsNonTailscale(string address)
    {
        Assert.Throws<InvalidOperationException>(() =>
            TailscaleNetwork.ValidateListenAddresses([IPAddress.Parse(address)], allowLoopback: false));
    }

    [Fact]
    public void ValidateListenAddresses_LoopbackOnlyInDevelopment()
    {
        TailscaleNetwork.ValidateListenAddresses([IPAddress.Loopback], allowLoopback: true);
        TailscaleNetwork.ValidateListenAddresses([IPAddress.Parse("100.100.1.2")], allowLoopback: false);
        Assert.Throws<InvalidOperationException>(() => TailscaleNetwork.ValidateListenAddresses([], allowLoopback: true));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("*")]
    [InlineData("pc.example.ts.net;*")]
    public void ValidateConfiguration_Production_RequiresSpecificAllowedHosts(string? allowedHosts)
    {
        var config = Config(("AllowedHosts", allowedHosts));

        Assert.Throws<InvalidOperationException>(() => TailscaleHosting.ValidateConfiguration(config, isDevelopment: false));
    }

    [Fact]
    public void ValidateConfiguration_Production_AcceptsMagicDnsName()
    {
        TailscaleHosting.ValidateConfiguration(Config(("AllowedHosts", "pc.example.ts.net")), isDevelopment: false);
    }

    [Fact]
    public void ValidateConfiguration_RejectsKestrelEndpointsFromConfig()
    {
        var config = Config(("AllowedHosts", "pc.example.ts.net"), ("Kestrel:Endpoints:Any:Url", "http://0.0.0.0:8444"));

        Assert.Throws<InvalidOperationException>(() => TailscaleHosting.ValidateConfiguration(config, isDevelopment: true));
    }

    [Theory]
    [InlineData("Agent:ShutdownDelaySeconds", "4")]
    [InlineData("Agent:ShutdownDelaySeconds", "601")]
    [InlineData("Agent:SleepDelaySeconds", "0")]
    [InlineData("Agent:Port", "0")]
    [InlineData("Agent:CertificatePath", "C:\\cert.crt")]
    public void AgentOptions_InvalidValues_Throw(string key, string value)
    {
        Assert.Throws<InvalidOperationException>(() => AgentOptions.Load(Config((key, value))));
    }

    [Fact]
    public void AgentOptions_Defaults_AreSafe()
    {
        var options = AgentOptions.Load(Config());

        Assert.Equal(8444, options.Port);
        Assert.False(options.DryRun);
        Assert.Equal(10, options.ShutdownDelaySeconds);
        Assert.Empty(options.ListenAddresses);
        Assert.EndsWith(Path.Combine("TailControl", "Agent", "config", "authorized-clients.json"), options.AuthorizedClientsPath, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AdminCli_AuthorizeListRevoke()
    {
        using var phone = ClientIdentity.Create();
        var options = new AgentOptions { DataDirectory = _directory };
        var output = new StringWriter();

        Assert.Equal(0, AuthorizedClientsCli.Run(["authorize-client", "--key", phone.PublicKey, "--label", "Telefon"], options.ToCliContext(isDevelopment: true), output, TimeProvider.System));
        Assert.Equal(1, AuthorizedClientsCli.Run(["authorize-client", "--key", phone.PublicKey, "--label", "Alt"], options.ToCliContext(isDevelopment: true), output, TimeProvider.System));
        Assert.Equal(0, AuthorizedClientsCli.Run(["list-clients"], options.ToCliContext(isDevelopment: true), output, TimeProvider.System));
        Assert.Contains(phone.ClientId, output.ToString(), StringComparison.Ordinal);

        Assert.Equal(0, AuthorizedClientsCli.Run(["revoke-client", "--id", phone.ClientId], options.ToCliContext(isDevelopment: true), output, TimeProvider.System));
        Assert.True(AuthorizedClientsFile.LoadOrEmpty(options.AuthorizedClientsPath).Find(phone.ClientId)!.Client.IsRevoked);
    }

    [Theory]
    [InlineData("authorize-client", "--label", "Telefon")]
    [InlineData("authorize-client", "--key", "AAAA", "--label", "Telefon")]
    [InlineData("revoke-client", "--id", "0123456789abcdef0123456789abcdef")]
    [InlineData("authorize-client", "pozitional")]
    public void AdminCli_InvalidInput_Fails(params string[] args)
    {
        var options = new AgentOptions { DataDirectory = _directory };

        Assert.NotEqual(0, AuthorizedClientsCli.Run(args, options.ToCliContext(isDevelopment: true), new StringWriter(), TimeProvider.System));
    }

    [Fact]
    public void AdminCli_IsAdminCommand_OnlyForKnownCommands()
    {
        Assert.True(AuthorizedClientsCli.IsCommand(["authorize-client"]));
        Assert.False(AuthorizedClientsCli.IsCommand([]));
        Assert.False(AuthorizedClientsCli.IsCommand(["--urls=http://0.0.0.0:1"]));
    }

    [Fact]
    public void AuditLog_WritesJsonLines_AndRotates()
    {
        var log = new AuditLog(Path.Combine(_directory, "audit.log"), maxBytes: 400);
        for (var i = 0; i < 5; i++)
        {
            log.Write(new AuditEntry(TestSupport.Start, "id", "Telefon\nfals", "100.64.0.2", "lock", "Executed", null));
        }

        Assert.True(File.Exists(log.Path + ".1"));
        Assert.All(log.ReadAll(), e => Assert.Equal("Telefon\nfals", e.ClientLabel));
        Assert.All(File.ReadAllLines(log.Path), l => Assert.StartsWith("{", l, StringComparison.Ordinal));
    }

    [Fact]
    public void PemCertificateProvider_ReloadsWhenFilesChange()
    {
        var certPath = Path.Combine(_directory, "host.crt");
        var keyPath = Path.Combine(_directory, "host.key");

        WritePem(certPath, keyPath, "CN=first", TestSupport.Start.UtcDateTime);
        var provider = new PemCertificateProvider(certPath, keyPath);
        var first = provider.GetCertificate();
        Assert.True(first.HasPrivateKey);
        Assert.Same(first, provider.GetCertificate());

        WritePem(certPath, keyPath, "CN=second", TestSupport.Start.UtcDateTime.AddMinutes(1));

        Assert.Equal("CN=second", provider.GetCertificate().Subject);
    }

    private static void WritePem(string certPath, string keyPath, string subject, DateTime writeTime)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest(subject, key, HashAlgorithmName.SHA256);
        using var cert = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        File.WriteAllText(certPath, cert.ExportCertificatePem());
        File.WriteAllText(keyPath, key.ExportPkcs8PrivateKeyPem());
        File.SetLastWriteTimeUtc(certPath, writeTime);
        File.SetLastWriteTimeUtc(keyPath, writeTime);
    }

    private static IConfiguration Config(params (string Key, string? Value)[] values) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(values.Select(v => new KeyValuePair<string, string?>(v.Key, v.Value)))
            .Build();
}
