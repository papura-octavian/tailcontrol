using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using TailControl.Agent.Locking;
using TailControl.Shared.Contracts;

namespace TailControl.Tests.Agent;

/// <summary>Brokerul real (named pipe), cu un helper fals conectat din test.</summary>
public sealed class LockBrokerTests : IAsyncLifetime
{
    private readonly string _pipeName = $"TailControl.Tests.{Guid.NewGuid():N}";
    private LockBroker _broker = null!;

    public async Task InitializeAsync()
    {
        _broker = new LockBroker(
            new LockBrokerOptions { PipeName = _pipeName, ReplyTimeout = TimeSpan.FromMilliseconds(500), AllowSessionZeroForTests = true },
            NullLogger<LockBroker>.Instance);
        await _broker.StartAsync(CancellationToken.None);
        await _broker.Listening.WaitAsync(TimeSpan.FromSeconds(30));
    }

    public async Task DisposeAsync()
    {
        await _broker.StopAsync(CancellationToken.None);
        _broker.Dispose();
    }

    [Fact]
    public async Task NoHelper_ReturnsNoSession()
    {
        Assert.Equal(LockOutcome.NoSession, await _broker.LockAllSessionsAsync(CancellationToken.None));
    }

    [Fact]
    public async Task HelperReplyingOk_ReturnsLocked()
    {
        using var helper = await FakeHelper.ConnectAsync(_pipeName, LockPipeProtocol.Ok);
        await WaitForSessionsAsync(1);

        Assert.Equal(LockOutcome.Locked, await _broker.LockAllSessionsAsync(CancellationToken.None));
        Assert.Equal([LockPipeProtocol.LockRequest], helper.Received);
    }

    [Fact]
    public async Task HelperReportingError_ReturnsFailed()
    {
        using var helper = await FakeHelper.ConnectAsync(_pipeName, "ERR 5");
        await WaitForSessionsAsync(1);

        Assert.Equal(LockOutcome.Failed, await _broker.LockAllSessionsAsync(CancellationToken.None));
    }

    [Fact]
    public async Task HelperNotReplying_TimesOutAsFailed()
    {
        using var helper = await FakeHelper.ConnectAsync(_pipeName, reply: null);
        await WaitForSessionsAsync(1);

        Assert.Equal(LockOutcome.Failed, await _broker.LockAllSessionsAsync(CancellationToken.None));
    }

    [Fact]
    public async Task HelperDisconnected_IsRemoved()
    {
        var helper = await FakeHelper.ConnectAsync(_pipeName, LockPipeProtocol.Ok);
        await WaitForSessionsAsync(1);

        helper.Dispose();
        await WaitForSessionsAsync(0);

        Assert.Equal(LockOutcome.NoSession, await _broker.LockAllSessionsAsync(CancellationToken.None));
    }

    [Fact]
    public void PipeSecurity_DeniesNetwork_AndDoesNotAllowEveryone()
    {
        var rules = LockBroker.CreatePipeSecurity()
            .GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier))
            .Cast<PipeAccessRule>()
            .ToList();

        var network = new SecurityIdentifier(WellKnownSidType.NetworkSid, null);
        var everyone = new SecurityIdentifier(WellKnownSidType.WorldSid, null);
        var authenticated = new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null);
        var interactive = new SecurityIdentifier(WellKnownSidType.InteractiveSid, null);

        Assert.Contains(rules, r => r.IdentityReference.Equals(network) && r.AccessControlType == AccessControlType.Deny);
        Assert.DoesNotContain(rules, r => r.IdentityReference.Equals(everyone) || r.IdentityReference.Equals(authenticated));
        // Utilizatorii interactivi pot doar citi/scrie, nu pot schimba ACL-ul sau crea instante proprii.
        const PipeAccessRights adminRights = PipeAccessRights.ChangePermissions | PipeAccessRights.TakeOwnership | PipeAccessRights.CreateNewInstance;
        var interactiveRule = Assert.Single(rules, r => r.IdentityReference.Equals(interactive));
        Assert.Equal(PipeAccessRights.ReadWrite, interactiveRule.PipeAccessRights & PipeAccessRights.ReadWrite);
        Assert.Equal((PipeAccessRights)0, interactiveRule.PipeAccessRights & adminRights);
    }

    private async Task WaitForSessionsAsync(int expected)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (_broker.ConnectedSessions != expected)
        {
            Assert.True(DateTime.UtcNow < deadline, $"Asteptam {expected} sesiuni, sunt {_broker.ConnectedSessions}.");
            await Task.Delay(20);
        }
    }

    /// <summary>Se comporta ca helper-ul de blocare, fara sa blocheze nimic.</summary>
    private sealed class FakeHelper : IDisposable
    {
        private readonly NamedPipeClientStream _pipe;
        private readonly CancellationTokenSource _cts = new();

        private FakeHelper(NamedPipeClientStream pipe) => _pipe = pipe;

        public List<string> Received { get; } = [];

        public static async Task<FakeHelper> ConnectAsync(string pipeName, string? reply)
        {
            var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            // Generos: pe runner-ele CI cu 2 nuclee, thread pool-ul poate fi ocupat de alte teste.
            await pipe.ConnectAsync(30_000);
            var helper = new FakeHelper(pipe);
            _ = helper.RunAsync(reply);
            return helper;
        }

        public void Dispose()
        {
            _cts.Cancel();
            _pipe.Dispose();
        }

        private async Task RunAsync(string? reply)
        {
            try
            {
                using var reader = new StreamReader(_pipe, new UTF8Encoding(false), false, leaveOpen: true);
                using var writer = new StreamWriter(_pipe, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true, NewLine = "\n" };
                while (await reader.ReadLineAsync(_cts.Token) is { } line)
                {
                    Received.Add(line);
                    if (reply is not null)
                    {
                        await writer.WriteLineAsync(reply);
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException)
            {
            }
        }
    }
}
