using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using System.Text.RegularExpressions;

namespace NetworkOptimizer.Storage.Models;

/// <summary>
/// An external OpenSpeedTest server used for WAN speed testing.
/// ServerId is baked into the deploy command as EXTERNAL_SERVER_ID and stored on results.
/// Name is the user-facing display name shown in the UI.
/// </summary>
public class ExternalSpeedTestServer
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public int Id { get; set; }

    /// <summary>Auto-generated slug used as EXTERNAL_SERVER_ID in the deployed container</summary>
    [Required]
    [MaxLength(50)]
    public string ServerId { get; set; } = string.Empty;

    /// <summary>User-facing display name (e.g. "VPS Chicago")</summary>
    [Required]
    [MaxLength(100)]
    public string Name { get; set; } = string.Empty;

    [Required]
    [MaxLength(255)]
    public string Host { get; set; } = string.Empty;

    public int Port { get; set; } = 3005;

    [MaxLength(10)]
    public string Scheme { get; set; } = "https";

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    [NotMapped]
    public bool IsConfigured => !string.IsNullOrWhiteSpace(Host);

    [NotMapped]
    public string Url => IsConfigured
        ? (Port == 443 || Port == 80)
            ? $"{Scheme}://{Host}"
            : $"{Scheme}://{Host}:{Port}"
        : "";

    /// <summary>
    /// Generate a URL-safe slug from a display name.
    /// Must produce identical output to the bash equivalent in deploy-external-speedtest.sh.
    /// </summary>
    public static string GenerateServerId(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return "external";

        var slug = name.ToLowerInvariant();
        slug = Regex.Replace(slug, @"[^a-z0-9]", "-");
        slug = Regex.Replace(slug, @"-+", "-");
        slug = slug.Trim('-');

        return string.IsNullOrEmpty(slug) ? "external" : slug;
    }
}
