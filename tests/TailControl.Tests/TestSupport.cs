using Microsoft.Extensions.Time.Testing;
using TailControl.Shared.Security;

namespace TailControl.Tests;

internal static class TestSupport
{
    public static readonly DateTimeOffset Start = new(2026, 10, 27, 12, 0, 0, TimeSpan.Zero);

    public static FakeTimeProvider NewClock() => new(Start);

    public static string NewTempPath(string name) =>
        Path.Combine(Path.GetTempPath(), "tailcontrol-tests", $"{Guid.NewGuid():N}-{name}");
}

internal sealed class InMemoryClientStore(AuthorizedClientList list) : IAuthorizedClientStore
{
    public AuthorizedClientList List { get; set; } = list;

    public AuthorizedClientEntry? Find(string clientId) => List.Find(clientId);
}

/// <summary>Modifica cererea dupa semnare, ca sa simuleze un atacator pe drum.</summary>
internal sealed class TamperHandler(Action<HttpRequestMessage> tamper) : DelegatingHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        tamper(request);
        return base.SendAsync(request, cancellationToken);
    }
}
