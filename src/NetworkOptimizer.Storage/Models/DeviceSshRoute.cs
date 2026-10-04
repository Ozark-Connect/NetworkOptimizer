using System.ComponentModel.DataAnnotations;

namespace NetworkOptimizer.Storage.Models;

/// <summary>
/// A device that takes the Gateway SSH credentials instead of Device SSH. Only gateway hardware
/// adopted as an access point (UX, UX7) gets a row, written the first time its Device SSH login is
/// refused and the Gateway SSH login is accepted. Kept so a restart does not cost a refused login
/// per device; removed again when the Gateway SSH login is refused.
/// </summary>
public class DeviceSshRoute
{
    [Key]
    public int Id { get; set; }

    /// <summary>The device's MAC, normalized to lower-case colon form. One row per device.</summary>
    [Required]
    [MaxLength(20)]
    public string DeviceMac { get; set; } = "";

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
