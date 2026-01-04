using Microsoft.EntityFrameworkCore;
using NetworkOptimizer.Core.Enums;

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
    public DbSet<ModemConfiguration> ModemConfigurations { get; set; }
    public DbSet<DeviceSshConfiguration> DeviceSshConfigurations { get; set; }
    public DbSet<Iperf3Result> Iperf3Results { get; set; }
    public DbSet<UniFiSshSettings> UniFiSshSettings { get; set; }
    public DbSet<GatewaySshSettings> GatewaySshSettings { get; set; }
    public DbSet<DismissedIssue> DismissedIssues { get; set; }
    public DbSet<SystemSetting> SystemSettings { get; set; }
    public DbSet<UniFiConnectionSettings> UniFiConnectionSettings { get; set; }
    public DbSet<SqmWanConfiguration> SqmWanConfigurations { get; set; }
    public DbSet<AdminSettings> AdminSettings { get; set; }
    public DbSet<ClientSpeedTestResult> ClientSpeedTestResults { get; set; }

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

        // ModemConfiguration configuration
        modelBuilder.Entity<ModemConfiguration>(entity =>
        {
            entity.ToTable("ModemConfigurations");
            entity.HasIndex(e => e.Host);
            entity.HasIndex(e => e.Enabled);
        });

        // DeviceSshConfiguration configuration
        modelBuilder.Entity<DeviceSshConfiguration>(entity =>
        {
            entity.ToTable("DeviceSshConfigurations");
            entity.HasIndex(e => e.Host);
            entity.HasIndex(e => e.Enabled);
            // Store DeviceType enum as string for backwards compatibility
            entity.Property(e => e.DeviceType)
                .HasConversion<string>()
                .HasMaxLength(50);
        });

        // Iperf3Result configuration
        modelBuilder.Entity<Iperf3Result>(entity =>
        {
            entity.ToTable("Iperf3Results");
            entity.HasIndex(e => e.DeviceHost);
            entity.HasIndex(e => e.TestTime);
            entity.HasIndex(e => new { e.DeviceHost, e.TestTime });
        });

        // UniFiSshSettings configuration (singleton - only one row)
        modelBuilder.Entity<UniFiSshSettings>(entity =>
        {
            entity.ToTable("UniFiSshSettings");
        });

        // GatewaySshSettings configuration (singleton - only one row)
        modelBuilder.Entity<GatewaySshSettings>(entity =>
        {
            entity.ToTable("GatewaySshSettings");
        });

        // DismissedIssue configuration
        modelBuilder.Entity<DismissedIssue>(entity =>
        {
            entity.ToTable("DismissedIssues");
            entity.HasIndex(e => e.IssueKey).IsUnique();
        });

        // SystemSetting configuration (key-value store)
        modelBuilder.Entity<SystemSetting>(entity =>
        {
            entity.ToTable("SystemSettings");
            entity.HasKey(e => e.Key);
        });

        // UniFiConnectionSettings configuration (singleton - only one row)
        modelBuilder.Entity<UniFiConnectionSettings>(entity =>
        {
            entity.ToTable("UniFiConnectionSettings");
        });

        // SqmWanConfiguration configuration (one row per WAN)
        modelBuilder.Entity<SqmWanConfiguration>(entity =>
        {
            entity.ToTable("SqmWanConfigurations");
            entity.HasIndex(e => e.WanNumber).IsUnique();
        });

        // AdminSettings configuration (singleton - only one row)
        modelBuilder.Entity<AdminSettings>(entity =>
        {
            entity.ToTable("AdminSettings");
        });

        // ClientSpeedTestResult configuration
        modelBuilder.Entity<ClientSpeedTestResult>(entity =>
        {
            entity.ToTable("ClientSpeedTestResults");
            entity.HasIndex(e => e.ClientIp);
            entity.HasIndex(e => e.ClientMac);
            entity.HasIndex(e => e.TestTime);
            entity.HasIndex(e => e.Source);
            entity.HasIndex(e => new { e.ClientIp, e.TestTime });
            // Store enum as string
            entity.Property(e => e.Source)
                .HasConversion<string>()
                .HasMaxLength(50);
        });
    }
}
