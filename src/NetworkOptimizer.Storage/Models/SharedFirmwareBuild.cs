using System.ComponentModel.DataAnnotations;

namespace NetworkOptimizer.Storage.Models;

/// <summary>
/// One device firmware build some site's console has offered, pooled across all sites in the main
/// database. Ubiquiti ungates builds per console, so a build one site was offered can be planned
/// at another site on the same channel before its own console is offered it.
/// </summary>
public class SharedFirmwareBuild
{
    /// <summary>Model code the build applies to (catalog base_model, e.g. "UP1").</summary>
    [MaxLength(50)]
    public string Model { get; set; } = string.Empty;

    /// <summary>Channel the offering site's devices were on: "release", "release-candidate", "beta".</summary>
    [MaxLength(32)]
    public string Channel { get; set; } = string.Empty;

    /// <summary>Dotted build string as the console reports it, e.g. "2.2.6.532".</summary>
    [MaxLength(64)]
    public string Version { get; set; } = string.Empty;

    /// <summary>Direct firmware image URL - the source for an SSH `upgrade &lt;url&gt;`.</summary>
    [Required]
    public string Url { get; set; } = string.Empty;

    [MaxLength(64)]
    public string? Md5Sum { get; set; }

    /// <summary>When any site first reported this build.</summary>
    public DateTime FirstSeenUtc { get; set; }

    /// <summary>The most recent catalog refresh that included it.</summary>
    public DateTime LastSeenUtc { get; set; }
}
