using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using TailControl.Shared.Contracts;
using TailControl.Shared.Validation;

namespace TailControl.Hub.Data;

public sealed class Device
{
    public int Id { get; set; }

    public required string Name { get; set; }

    /// <summary>Normalizat <c>AA:BB:CC:DD:EE:FF</c>; <c>null</c> = fara Wake-on-LAN.</summary>
    public string? MacAddress { get; set; }

    /// <summary>Numele MagicDNS complet, litere mici.</summary>
    public required string Host { get; set; }

    public int AgentPort { get; set; }

    public DeviceKind Kind { get; set; }

    public DateTimeOffset CreatedUtc { get; set; }

    public DateTimeOffset UpdatedUtc { get; set; }

    /// <summary>Persistat rar (vezi <see cref="Status.StatusMonitor"/>), ca sa nu uzam cardul SD.</summary>
    public DateTimeOffset? LastSeenUtc { get; set; }
}

public sealed class AuditRecord
{
    public long Id { get; set; }

    public DateTimeOffset TimeUtc { get; set; }

    public required string ClientId { get; set; }

    public required string ClientLabel { get; set; }

    public string? RemoteIp { get; set; }

    public required string Action { get; set; }

    public string? Target { get; set; }

    public required string Result { get; set; }

    public string? Detail { get; set; }
}

public sealed class HubDb(DbContextOptions<HubDb> options) : DbContext(options)
{
    public DbSet<Device> Devices => Set<Device>();

    public DbSet<AuditRecord> AuditLog => Set<AuditRecord>();

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        // SQLite nu poate ordona/compara DateTimeOffset nativ; il stocam ca numar.
        configurationBuilder.Properties<DateTimeOffset>().HaveConversion<DateTimeOffsetToBinaryConverter>();
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Device>(e =>
        {
            e.ToTable("Devices");
            e.Property(d => d.Name).HasMaxLength(DeviceValidation.MaxNameLength).UseCollation("NOCASE");
            e.Property(d => d.MacAddress).HasMaxLength(17);
            e.Property(d => d.Host).HasMaxLength(DeviceValidation.MaxHostLength);
            e.Property(d => d.Kind).HasConversion<string>().HasMaxLength(16);
            e.HasIndex(d => d.Name).IsUnique();
            e.HasIndex(d => d.Host).IsUnique();
        });

        modelBuilder.Entity<AuditRecord>(e =>
        {
            e.ToTable("AuditLog");
            e.Property(a => a.ClientId).HasMaxLength(64);
            e.Property(a => a.ClientLabel).HasMaxLength(64);
            e.Property(a => a.RemoteIp).HasMaxLength(64);
            e.Property(a => a.Action).HasMaxLength(32);
            e.Property(a => a.Target).HasMaxLength(64);
            e.Property(a => a.Result).HasMaxLength(32);
            e.Property(a => a.Detail).HasMaxLength(256);
            e.HasIndex(a => a.TimeUtc);
        });
    }
}

/// <summary>Folosit doar de <c>dotnet ef migrations</c>, ca sa nu porneasca serverul.</summary>
public sealed class HubDbDesignTimeFactory : IDesignTimeDbContextFactory<HubDb>
{
    public HubDb CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<HubDb>().UseSqlite("Data Source=design-time.db").Options);
}
