using System.Text.Json.Nodes;
using TailControl.Shared.Security;

namespace TailControl.Tests;

public sealed class AuthorizedClientsTests : IDisposable
{
    private readonly ClientIdentity _phone = ClientIdentity.Create();
    private readonly ClientIdentity _laptop = ClientIdentity.Create();
    private readonly string _path = TestSupport.NewTempPath("clients.json");

    public AuthorizedClientsTests() => Directory.CreateDirectory(Path.GetDirectoryName(_path)!);

    public void Dispose()
    {
        _phone.Dispose();
        _laptop.Dispose();
        TestSupport.DeleteQuietly(_path);
    }

    [Fact]
    public void Authorize_SerializeParse_RoundTrips()
    {
        var list = AuthorizedClientList.Empty
            .Authorize(_phone.PublicKey, "Telefon", isAdmin: true, TestSupport.Start, out var phone)
            .Authorize(_laptop.PublicKey, "Laptop", isAdmin: false, TestSupport.Start.AddMinutes(1), out _);

        var parsed = AuthorizedClientList.Parse(list.Serialize());

        Assert.Equal(_phone.ClientId, phone.Id);
        Assert.Equal(2, parsed.Clients.Count);
        Assert.True(parsed.Find(_phone.ClientId)!.Client.IsAdmin);
        Assert.False(parsed.Find(_laptop.ClientId)!.Client.IsAdmin);
        Assert.Equal("Laptop", parsed.Find(_laptop.ClientId)!.Client.Label);
    }

    [Fact]
    public void Authorize_SameKeyTwice_Throws()
    {
        var list = AuthorizedClientList.Empty.Authorize(_phone.PublicKey, "Telefon", true, TestSupport.Start, out _);

        Assert.Throws<InvalidOperationException>(() => list.Authorize(_phone.PublicKey, "Alt nume", true, TestSupport.Start, out _));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("rand\nnou")]
    [InlineData("o eticheta mult prea lunga care depaseste limita de saizeci si patru de caractere")]
    public void Authorize_InvalidLabel_Throws(string label)
    {
        Assert.Throws<InvalidDataException>(() =>
            AuthorizedClientList.Empty.Authorize(_phone.PublicKey, label, false, TestSupport.Start, out _));
    }

    [Fact]
    public void Authorize_InvalidKey_Throws()
    {
        Assert.Throws<ArgumentException>(() =>
            AuthorizedClientList.Empty.Authorize("AAAA", "Telefon", false, TestSupport.Start, out _));
    }

    [Fact]
    public void Revoke_MarksClientRevoked_AndSurvivesRoundTrip()
    {
        var list = AuthorizedClientList.Empty
            .Authorize(_phone.PublicKey, "Telefon", true, TestSupport.Start, out _)
            .Revoke(_phone.ClientId, TestSupport.Start.AddDays(1));

        var parsed = AuthorizedClientList.Parse(list.Serialize());

        Assert.True(parsed.Find(_phone.ClientId)!.Client.IsRevoked);
        Assert.Equal(TestSupport.Start.AddDays(1), parsed.Find(_phone.ClientId)!.Client.RevokedUtc);
    }

    [Fact]
    public void Revoke_UnknownClient_Throws()
    {
        Assert.Throws<InvalidOperationException>(() => AuthorizedClientList.Empty.Revoke(_phone.ClientId, TestSupport.Start));
    }

    [Fact]
    public void Parse_IdNotMatchingKey_Throws()
    {
        // Cineva schimba cheia publica dar lasa id-ul vechi (sau invers).
        var node = JsonNode.Parse(AuthorizedClientList.Empty
            .Authorize(_phone.PublicKey, "Telefon", true, TestSupport.Start, out _)
            .Serialize())!;
        node["clients"]![0]!["publicKey"] = _laptop.PublicKey;

        Assert.Throws<InvalidDataException>(() => AuthorizedClientList.Parse(node.ToJsonString()));
    }

    [Theory]
    [InlineData("nu e json")]
    [InlineData("{\"version\":2,\"clients\":[]}")]
    [InlineData("{\"version\":1,\"clients\":[],\"extra\":true}")]
    [InlineData("null")]
    public void Parse_InvalidFile_Throws(string json)
    {
        Assert.Throws<InvalidDataException>(() => AuthorizedClientList.Parse(json));
    }

    [Fact]
    public void File_Missing_AuthorizesNobody()
    {
        var file = new AuthorizedClientsFile(_path);

        Assert.Null(file.Find(_phone.ClientId));
        Assert.NotNull(file.LastError);
    }

    [Fact]
    public void File_Corrupt_AuthorizesNobody()
    {
        AuthorizedClientsFile.Save(_path, AuthorizedClientList.Empty.Authorize(_phone.PublicKey, "Telefon", true, TestSupport.Start, out _));
        var file = new AuthorizedClientsFile(_path);
        Assert.NotNull(file.Find(_phone.ClientId));

        File.AppendAllText(_path, "{ stricat");

        Assert.Null(file.Find(_phone.ClientId));
        Assert.NotNull(file.LastError);
    }

    // Testele de blocare sunt deterministe: blocarea se elibereaza din callback-ul de reincercare,
    // nu dupa un timer, deci nu depind de cat de ocupat e thread pool-ul (runner-e CI cu 2 nuclee).

    [Fact]
    public void Save_TargetLocked_RetriesAndSucceedsAfterRelease()
    {
        // Simuleaza un antivirus care tine fisierul deschis exclusiv in momentul inlocuirii.
        AuthorizedClientsFile.Save(_path, AuthorizedClientList.Empty);
        var locked = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.None);
        var retries = new List<int>();

        try
        {
            AuthorizedClientsFile.Save(
                _path,
                AuthorizedClientList.Empty.Authorize(_phone.PublicKey, "Telefon", true, TestSupport.Start, out _),
                beforeRetry: attempt =>
                {
                    retries.Add(attempt);
                    locked.Dispose();
                });
        }
        finally
        {
            locked.Dispose();
        }

        Assert.Equal([1], retries);
        Assert.NotNull(AuthorizedClientList.Parse(File.ReadAllText(_path)).Find(_phone.ClientId));
    }

    [Fact]
    public void Save_TargetLockedTooLong_GivesUpAfterMaxAttempts()
    {
        AuthorizedClientsFile.Save(_path, AuthorizedClientList.Empty);
        var retries = new List<int>();

        using (new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var ex = Record.Exception(() => AuthorizedClientsFile.Save(
                _path,
                AuthorizedClientList.Empty.Authorize(_phone.PublicKey, "Telefon", true, TestSupport.Start, out _),
                beforeRetry: retries.Add));

            Assert.True(ex is IOException or UnauthorizedAccessException, $"Exceptie neasteptata: {ex}");
        }

        Assert.Equal(Enumerable.Range(1, AuthorizedClientsFile.SaveAttempts - 1), retries);
        Assert.Empty(AuthorizedClientList.Parse(File.ReadAllText(_path)).Clients);
    }

    [Fact]
    public void File_ChangedOnDisk_IsReloaded()
    {
        var list = AuthorizedClientList.Empty.Authorize(_phone.PublicKey, "Telefon", true, TestSupport.Start, out _);
        AuthorizedClientsFile.Save(_path, list);
        var file = new AuthorizedClientsFile(_path);
        Assert.False(file.Find(_phone.ClientId)!.Client.IsRevoked);

        AuthorizedClientsFile.Save(_path, list.Revoke(_phone.ClientId, TestSupport.Start));

        Assert.True(file.Find(_phone.ClientId)!.Client.IsRevoked);
        Assert.Null(file.LastError);
    }
}
