using System.ComponentModel.DataAnnotations;

namespace NetworkOptimizer.Storage.Models;

/// <summary>
/// Represents a managed site/customer in Network Optimizer.
/// Each site has its own UniFi controller connection and isolated data.
/// </summary>
public class Site
{
    [Key]
    public int Id { get; set; }

    /// <summary>User-defined name for this site (e.g., "Acme Corp", "Home Network")</summary>
    [Required]
    [MaxLength(100)]
    public string Name { get; set; } = "";

    /// <summary>Optional longer display name</summary>
    [MaxLength(200)]
    public string? DisplayName { get; set; }

    /// <summary>Whether this site is enabled for operations</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Sort order for UI display (lower = first)</summary>
    public int SortOrder { get; set; } = 0;

    /// <summary>Optional notes about this site</summary>
    [MaxLength(1000)]
    public string? Notes { get; set; }

    /// <summary>When this site was created</summary>
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>When this site was last updated</summary>
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    // Navigation properties - 1:1 relationships
    public UniFiConnectionSettings? ConnectionSettings { get; set; }
    public UniFiSshSettings? UniFiSshSettings { get; set; }
    public GatewaySshSettings? GatewaySshSettings { get; set; }

    // Navigation properties - 1:many relationships
    public ICollection<AuditResult> AuditResults { get; set; } = new List<AuditResult>();
    public ICollection<SqmBaseline> SqmBaselines { get; set; } = new List<SqmBaseline>();
    public ICollection<Iperf3Result> Iperf3Results { get; set; } = new List<Iperf3Result>();
    public ICollection<DismissedIssue> DismissedIssues { get; set; } = new List<DismissedIssue>();
    public ICollection<SqmWanConfiguration> SqmWanConfigurations { get; set; } = new List<SqmWanConfiguration>();
    public ICollection<ModemConfiguration> ModemConfigurations { get; set; } = new List<ModemConfiguration>();
    public ICollection<DeviceSshConfiguration> DeviceSshConfigurations { get; set; } = new List<DeviceSshConfiguration>();
    public ICollection<UpnpNote> UpnpNotes { get; set; } = new List<UpnpNote>();
}
