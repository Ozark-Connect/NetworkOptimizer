using Microsoft.EntityFrameworkCore;
using NetworkOptimizer.Alerts.Models;
using NetworkOptimizer.Threats.Models;

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
    public DbSet<UpnpNote> UpnpNotes { get; set; }
    public DbSet<ApLocation> ApLocations { get; set; }
    public DbSet<Building> Buildings { get; set; }
    public DbSet<FloorPlan> FloorPlans { get; set; }
    public DbSet<PlannedAp> PlannedAps { get; set; }
    public DbSet<FloorPlanImage> FloorPlanImages { get; set; }
    public DbSet<ClientSignalLog> ClientSignalLogs { get; set; }
    public DbSet<AlertRule> AlertRules { get; set; }
    public DbSet<DeliveryChannel> DeliveryChannels { get; set; }
    public DbSet<AlertHistoryEntry> AlertHistory { get; set; }
    public DbSet<AlertIncident> AlertIncidents { get; set; }
    public DbSet<ThreatEvent> ThreatEvents { get; set; }
    public DbSet<ThreatPattern> ThreatPatterns { get; set; }
    public DbSet<CrowdSecReputation> CrowdSecReputations { get; set; }

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
            entity.HasIndex(e => e.Direction);
            entity.HasIndex(e => new { e.DeviceHost, e.TestTime });
            entity.Property(e => e.Direction).HasConversion<int>();
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

        // UpnpNote configuration
        modelBuilder.Entity<UpnpNote>(entity =>
        {
            entity.ToTable("UpnpNotes");
            entity.HasIndex(e => new { e.HostIp, e.Port, e.Protocol }).IsUnique();
        });

        // ApLocation configuration (one per AP MAC)
        modelBuilder.Entity<ApLocation>(entity =>
        {
            entity.ToTable("ApLocations");
            entity.HasIndex(e => e.ApMac).IsUnique();
        });

        // Building configuration
        modelBuilder.Entity<Building>(entity =>
        {
            entity.ToTable("Buildings");
            entity.HasMany(e => e.Floors)
                .WithOne(e => e.Building)
                .HasForeignKey(e => e.BuildingId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // FloorPlan configuration
        modelBuilder.Entity<FloorPlan>(entity =>
        {
            entity.ToTable("FloorPlans");
            entity.HasIndex(e => e.BuildingId);
            entity.HasMany(e => e.Images)
                .WithOne(e => e.FloorPlan)
                .HasForeignKey(e => e.FloorPlanId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // FloorPlanImage configuration
        modelBuilder.Entity<FloorPlanImage>(entity =>
        {
            entity.ToTable("FloorPlanImages");
            entity.HasIndex(e => e.FloorPlanId);
        });

        // PlannedAp configuration
        modelBuilder.Entity<PlannedAp>(entity =>
        {
            entity.ToTable("PlannedAps");
        });

        // ClientSignalLog configuration
        modelBuilder.Entity<ClientSignalLog>(entity =>
        {
            entity.ToTable("ClientSignalLogs");
            entity.HasIndex(e => new { e.ClientMac, e.Timestamp });
            entity.HasIndex(e => e.TraceHash);
        });

        // AlertRule configuration
        modelBuilder.Entity<AlertRule>(entity =>
        {
            entity.ToTable("AlertRules");
            entity.Property(e => e.MinSeverity).HasConversion<int>();
            entity.Property(e => e.EscalationSeverity).HasConversion<int>();
        });

        // DeliveryChannel configuration
        modelBuilder.Entity<DeliveryChannel>(entity =>
        {
            entity.ToTable("DeliveryChannels");
            entity.Property(e => e.ChannelType).HasConversion<int>();
            entity.Property(e => e.MinSeverity).HasConversion<int>();
        });

        // AlertHistoryEntry configuration
        modelBuilder.Entity<AlertHistoryEntry>(entity =>
        {
            entity.ToTable("AlertHistory");
            entity.HasIndex(e => e.TriggeredAt);
            entity.HasIndex(e => new { e.Source, e.TriggeredAt });
            entity.HasIndex(e => e.Status);
            entity.HasIndex(e => e.RuleId);
            entity.HasIndex(e => e.IncidentId);
            entity.Property(e => e.Severity).HasConversion<int>();
            entity.Property(e => e.Status).HasConversion<int>();
        });

        // AlertIncident configuration
        modelBuilder.Entity<AlertIncident>(entity =>
        {
            entity.ToTable("AlertIncidents");
            entity.HasIndex(e => e.CorrelationKey);
            entity.HasIndex(e => e.Status);
            entity.Property(e => e.Severity).HasConversion<int>();
            entity.Property(e => e.Status).HasConversion<int>();
        });

        // ThreatEvent configuration
        modelBuilder.Entity<ThreatEvent>(entity =>
        {
            entity.ToTable("ThreatEvents");
            entity.HasIndex(e => e.Timestamp);
            entity.HasIndex(e => new { e.SourceIp, e.Timestamp });
            entity.HasIndex(e => new { e.DestPort, e.Timestamp });
            entity.HasIndex(e => e.KillChainStage);
            entity.HasIndex(e => e.InnerAlertId).IsUnique();
            entity.HasIndex(e => e.EventSource);
            entity.Property(e => e.Action).HasConversion<int>();
            entity.Property(e => e.KillChainStage).HasConversion<int>();
            entity.Property(e => e.EventSource).HasConversion<int>();
            entity.HasOne(e => e.Pattern)
                .WithMany(p => p.Events)
                .HasForeignKey(e => e.PatternId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        // ThreatPattern configuration
        modelBuilder.Entity<ThreatPattern>(entity =>
        {
            entity.ToTable("ThreatPatterns");
            entity.HasIndex(e => new { e.PatternType, e.DetectedAt });
            entity.Property(e => e.PatternType).HasConversion<int>();
        });

        // CrowdSecReputation configuration
        modelBuilder.Entity<CrowdSecReputation>(entity =>
        {
            entity.ToTable("CrowdSecReputations");
            entity.HasKey(e => e.Ip);
            entity.HasIndex(e => e.ExpiresAt);
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
