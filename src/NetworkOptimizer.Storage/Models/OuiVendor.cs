using System.ComponentModel.DataAnnotations;

namespace NetworkOptimizer.Storage.Models;

public class OuiVendor
{
    [Key, MaxLength(8)]
    public string OuiPrefix { get; set; } = string.Empty;

    [Required, MaxLength(200)]
    public string VendorName { get; set; } = string.Empty;

    public DateTime LastUpdated { get; set; } = DateTime.UtcNow;
}
