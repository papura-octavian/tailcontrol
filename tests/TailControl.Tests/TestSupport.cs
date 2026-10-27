using Microsoft.Extensions.Time.Testing;
using TailControl.Shared.Security;

namespace TailControl.Tests;

internal static class TestSupport
{
    public static readonly DateTimeOffset Start = new(2026, 10, 27, 12, 0, 0, TimeSpan.Zero);

    public static FakeTimeProvider NewClock() => new(Start);

    public static string NewTempPath(string name) =>
        Path.Combine(Path.GetTempPath(), "tailcontrol-tests", $"{Guid.NewGuid():N}-{name}");

    /// <summary>
    /// Sterge un fisier sau folder temporar de test. Pe runner-ele Windows din CI, un fisier proaspat
    /// creat (ex. baza SQLite) poate ramane blocat cateva momente de antivirus. Reincercam, iar daca
    /// tot nu merge il lasam in Temp: curatenia nu trebuie sa pice un test care a trecut.
    /// </summary>
    public static void DeleteQuietly(string path)
    {
        for (var attempt = 0; attempt < 10; attempt++)
        {
            try
            {
                if (Directory.Exists(path))
                {
                    Directory.Delete(path, recursive: true);
                }
                else if (File.Exists(path))
                {
                    File.Delete(path);
                }

                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Thread.Sleep(200);
            }
        }
    }
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
