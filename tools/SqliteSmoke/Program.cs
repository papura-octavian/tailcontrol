using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

// Implicit doar pe loopback; se poate suprascrie cu --urls.
if (string.IsNullOrEmpty(builder.Configuration["urls"]))
{
    builder.WebHost.UseUrls("http://127.0.0.1:8443");
}

var dbPath = Path.GetFullPath(builder.Configuration["Db"] ?? "smoke.db");
builder.Services.AddDbContext<SmokeDb>(o => o.UseSqlite($"Data Source={dbPath}"));

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    scope.ServiceProvider.GetRequiredService<SmokeDb>().Database.EnsureCreated();
}

// Scrie un rand, apoi il citeste inapoi si raporteaza totalul.
app.MapGet("/health", async (SmokeDb db) =>
{
    var sw = Stopwatch.StartNew();

    var ping = new Ping { CreatedUtc = DateTime.UtcNow };
    db.Pings.Add(ping);
    await db.SaveChangesAsync();

    var readBack = await db.Pings.AsNoTracking().SingleAsync(p => p.Id == ping.Id);
    var total = await db.Pings.CountAsync();
    var sqliteVersion = await db.Database.SqlQueryRaw<string>("SELECT sqlite_version() AS Value").SingleAsync();

    sw.Stop();

    return Results.Ok(new
    {
        status = readBack.CreatedUtc == ping.CreatedUtc ? "ok" : "mismatch",
        rows = total,
        lastId = readBack.Id,
        sqliteVersion,
        nativeLibrary = SQLitePCL.raw.GetNativeLibraryName(),
        runtime = RuntimeInformation.FrameworkDescription,
        rid = RuntimeInformation.RuntimeIdentifier,
        arch = RuntimeInformation.ProcessArchitecture.ToString(),
        workingSetMb = Math.Round(Environment.WorkingSet / 1024.0 / 1024.0, 1),
        elapsedMs = sw.ElapsedMilliseconds,
        dbPath,
    });
});

app.Run();

internal sealed class SmokeDb(DbContextOptions<SmokeDb> options) : DbContext(options)
{
    public DbSet<Ping> Pings => Set<Ping>();
}

internal sealed class Ping
{
    public int Id { get; set; }
    public DateTime CreatedUtc { get; set; }
}
