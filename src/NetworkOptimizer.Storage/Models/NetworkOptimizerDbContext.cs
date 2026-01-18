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

    // Site - parent entity for multi-site support
    public DbSet<Site> Sites { get; set; }

    // Site-scoped entities
    public DbSet<AuditResult> AuditResults { get; set; }
    public DbSet<SqmBaseline> SqmBaselines { get; set; }
    public DbSet<ModemConfiguration> ModemConfigurations { get; set; }
    public DbSet<DeviceSshConfiguration> DeviceSshConfigurations { get; set; }
    public DbSet<Iperf3Result> Iperf3Results { get; set; }
    public DbSet<UniFiSshSettings> UniFiSshSettings { get; set; }
    public DbSet<GatewaySshSettings> GatewaySshSettings { get; set; }
    public DbSet<DismissedIssue> DismissedIssues { get; set; }
    public DbSet<UniFiConnectionSettings> UniFiConnectionSettings { get; set; }
    public DbSet<SqmWanConfiguration> SqmWanConfigurations { get; set; }
    public DbSet<UpnpNote> UpnpNotes { get; set; }

    // Global entities (not site-scoped)
    public DbSet<AgentConfiguration> AgentConfigurations { get; set; }
    public DbSet<LicenseInfo> Licenses { get; set; }
    public DbSet<SystemSetting> SystemSettings { get; set; }
    public DbSet<AdminSettings> AdminSettings { get; set; }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // ============================================
        // Site configuration (parent entity)
        // ============================================
        modelBuilder.Entity<Site>(entity =>
        {
            entity.ToTable("Sites");
            entity.HasIndex(e => e.Name);
            entity.HasIndex(e => e.Enabled);
            entity.HasIndex(e => e.SortOrder);

            // 1:1 relationship with UniFiConnectionSettings
            entity.HasOne(s => s.ConnectionSettings)
                .WithOne(c => c.Site)
                .HasForeignKey<UniFiConnectionSettings>(c => c.SiteId)
                .OnDelete(DeleteBehavior.Cascade);

            // 1:1 relationship with UniFiSshSettings
            entity.HasOne(s => s.UniFiSshSettings)
                .WithOne(c => c.Site)
                .HasForeignKey<UniFiSshSettings>(c => c.SiteId)
                .OnDelete(DeleteBehavior.Cascade);

            // 1:1 relationship with GatewaySshSettings
            entity.HasOne(s => s.GatewaySshSettings)
                .WithOne(c => c.Site)
                .HasForeignKey<GatewaySshSettings>(c => c.SiteId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // ============================================
        // Site-scoped entities (1:many relationships)
        // ============================================

        // AuditResult configuration
        modelBuilder.Entity<AuditResult>(entity =>
        {
            entity.ToTable("AuditResults");
            entity.HasIndex(e => e.SiteId);
            entity.HasIndex(e => e.DeviceId);
            entity.HasIndex(e => e.AuditDate);
            entity.HasIndex(e => new { e.SiteId, e.AuditDate });
            entity.HasIndex(e => new { e.SiteId, e.DeviceId, e.AuditDate });

            entity.HasOne(e => e.Site)
                .WithMany(s => s.AuditResults)
                .HasForeignKey(e => e.SiteId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // SqmBaseline configuration
        modelBuilder.Entity<SqmBaseline>(entity =>
        {
            entity.ToTable("SqmBaselines");
            entity.HasIndex(e => e.SiteId);
            entity.HasIndex(e => e.DeviceId);
            entity.HasIndex(e => e.InterfaceId);
            entity.HasIndex(e => new { e.SiteId, e.DeviceId, e.InterfaceId }).IsUnique();
            entity.HasIndex(e => e.BaselineStart);

            entity.HasOne(e => e.Site)
                .WithMany(s => s.SqmBaselines)
                .HasForeignKey(e => e.SiteId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // Iperf3Result configuration
        modelBuilder.Entity<Iperf3Result>(entity =>
        {
            entity.ToTable("Iperf3Results");
            entity.HasIndex(e => e.SiteId);
            entity.HasIndex(e => e.DeviceHost);
            entity.HasIndex(e => e.TestTime);
            entity.HasIndex(e => e.Direction);
            entity.HasIndex(e => new { e.SiteId, e.TestTime });
            entity.HasIndex(e => new { e.SiteId, e.DeviceHost, e.TestTime });
            entity.Property(e => e.Direction).HasConversion<int>();

            entity.HasOne(e => e.Site)
                .WithMany(s => s.Iperf3Results)
                .HasForeignKey(e => e.SiteId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // DismissedIssue configuration
        modelBuilder.Entity<DismissedIssue>(entity =>
        {
            entity.ToTable("DismissedIssues");
            entity.HasIndex(e => e.SiteId);
            entity.HasIndex(e => new { e.SiteId, e.IssueKey }).IsUnique();

            entity.HasOne(e => e.Site)
                .WithMany(s => s.DismissedIssues)
                .HasForeignKey(e => e.SiteId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // SqmWanConfiguration configuration
        modelBuilder.Entity<SqmWanConfiguration>(entity =>
        {
            entity.ToTable("SqmWanConfigurations");
            entity.HasIndex(e => e.SiteId);
            entity.HasIndex(e => new { e.SiteId, e.WanNumber }).IsUnique();

            entity.HasOne(e => e.Site)
                .WithMany(s => s.SqmWanConfigurations)
                .HasForeignKey(e => e.SiteId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // ModemConfiguration configuration
        modelBuilder.Entity<ModemConfiguration>(entity =>
        {
            entity.ToTable("ModemConfigurations");
            entity.HasIndex(e => e.SiteId);
            entity.HasIndex(e => e.Host);
            entity.HasIndex(e => e.Enabled);

            entity.HasOne(e => e.Site)
                .WithMany(s => s.ModemConfigurations)
                .HasForeignKey(e => e.SiteId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // DeviceSshConfiguration configuration
        modelBuilder.Entity<DeviceSshConfiguration>(entity =>
        {
            entity.ToTable("DeviceSshConfigurations");
            entity.HasIndex(e => e.SiteId);
            entity.HasIndex(e => e.Host);
            entity.HasIndex(e => e.Enabled);
            // Store DeviceType enum as string for backwards compatibility
            entity.Property(e => e.DeviceType)
                .HasConversion<string>()
                .HasMaxLength(50);

            entity.HasOne(e => e.Site)
                .WithMany(s => s.DeviceSshConfigurations)
                .HasForeignKey(e => e.SiteId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // UpnpNote configuration
        modelBuilder.Entity<UpnpNote>(entity =>
        {
            entity.ToTable("UpnpNotes");
            entity.HasIndex(e => e.SiteId);
            entity.HasIndex(e => new { e.SiteId, e.HostIp, e.Port, e.Protocol }).IsUnique();

            entity.HasOne(e => e.Site)
                .WithMany(s => s.UpnpNotes)
                .HasForeignKey(e => e.SiteId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // ============================================
        // 1:1 Site-scoped entities (configured via Site)
        // ============================================

        // UniFiConnectionSettings configuration
        modelBuilder.Entity<UniFiConnectionSettings>(entity =>
        {
            entity.ToTable("UniFiConnectionSettings");
            entity.HasIndex(e => e.SiteId).IsUnique();
        });

        // UniFiSshSettings configuration
        modelBuilder.Entity<UniFiSshSettings>(entity =>
        {
            entity.ToTable("UniFiSshSettings");
            entity.HasIndex(e => e.SiteId).IsUnique();
        });

        // GatewaySshSettings configuration
        modelBuilder.Entity<GatewaySshSettings>(entity =>
        {
            entity.ToTable("GatewaySshSettings");
            entity.HasIndex(e => e.SiteId).IsUnique();
        });

        // ============================================
        // Global entities (not site-scoped)
        // ============================================

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

        // SystemSetting configuration (key-value store)
        modelBuilder.Entity<SystemSetting>(entity =>
        {
            entity.ToTable("SystemSettings");
            entity.HasKey(e => e.Key);
        });

        // AdminSettings configuration (singleton - only one row)
        modelBuilder.Entity<AdminSettings>(entity =>
        {
            entity.ToTable("AdminSettings");
        });
    }
}

/// <summary>
/// Custom DbContext factory for singleton services that need database access.
/// </summary>
/// <remarks>
/// This exists to work around a DI lifetime conflict: AddDbContext registers DbContextOptions
/// as Scoped, but AddDbContextFactory needs Singleton options. Using both causes validation
/// errors in Development mode. This factory owns its own options instance, avoiding the conflict.
/// See Program.cs registration for details.
/// </remarks>
public class NetworkOptimizerDbContextFactory : IDbContextFactory<NetworkOptimizerDbContext>
{
    private readonly DbContextOptions<NetworkOptimizerDbContext> _options;

    public NetworkOptimizerDbContextFactory(DbContextOptions<NetworkOptimizerDbContext> options)
    {
        _options = options;
    }

    public NetworkOptimizerDbContext CreateDbContext()
    {
        return new NetworkOptimizerDbContext(_options);
    }
}
