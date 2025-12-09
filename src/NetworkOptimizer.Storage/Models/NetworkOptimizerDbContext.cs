using Microsoft.EntityFrameworkCore;

namespace NetworkOptimizer.Storage.Models;

/// <summary>
/// Entity Framework DbContext for NetworkOptimizer local storage
/// </summary>
public class NetworkOptimizerDbContext : DbContext
{
    public NetworkOptimizerDbContext(DbContextOptions<NetworkOptimizerDbContext> options)
        : base(options)
    {
    }

    public DbSet<AuditResult> AuditResults { get; set; }
    public DbSet<SqmBaseline> SqmBaselines { get; set; }
    public DbSet<AgentConfiguration> AgentConfigurations { get; set; }
    public DbSet<LicenseInfo> Licenses { get; set; }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // AuditResult configuration
        modelBuilder.Entity<AuditResult>(entity =>
        {
            entity.ToTable("AuditResults");
            entity.HasIndex(e => e.DeviceId);
            entity.HasIndex(e => e.AuditDate);
            entity.HasIndex(e => new { e.DeviceId, e.AuditDate });
        });

        // SqmBaseline configuration
        modelBuilder.Entity<SqmBaseline>(entity =>
        {
            entity.ToTable("SqmBaselines");
            entity.HasIndex(e => e.DeviceId);
            entity.HasIndex(e => e.InterfaceId);
            entity.HasIndex(e => new { e.DeviceId, e.InterfaceId }).IsUnique();
            entity.HasIndex(e => e.BaselineStart);
        });

        // AgentConfiguration configuration
        modelBuilder.Entity<AgentConfiguration>(entity =>
        {
            entity.ToTable("AgentConfigurations");
            entity.HasIndex(e => e.IsEnabled);
            entity.HasIndex(e => e.LastSeenAt);
        });

        // LicenseInfo configuration
        modelBuilder.Entity<LicenseInfo>(entity =>
        {
            entity.ToTable("Licenses");
            entity.HasIndex(e => e.LicenseKey).IsUnique();
            entity.HasIndex(e => e.IsActive);
            entity.HasIndex(e => e.ExpirationDate);
        });
    }
}
