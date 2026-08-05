using System.ComponentModel.DataAnnotations;

namespace NetworkOptimizer.Storage.Models.Identity;

/// <summary>
/// How many times one user has been shown a particular teaching hint, so a hint that exists only
/// to reveal a non-obvious gesture can stop repeating once they plainly know it.
/// <para>
/// Per USER rather than per site or per install: what someone has learned travels with them across
/// every site they can see, and one operator learning a gesture says nothing about their
/// colleagues. Site-scoped state lives in SystemSettings and install-wide state in AdminSettings;
/// neither can answer "has this person seen it".
/// </para>
/// <para>
/// The pattern is deliberately general - key it, count it, stop at the threshold - so the next
/// hint that wears out its welcome does not need its own table or its own flag. Nothing here is
/// security-relevant: losing a row costs the user one extra tooltip.
/// </para>
/// </summary>
public class UserUiHint
{
    public int Id { get; set; }

    /// <summary>The Identity user this count belongs to (<see cref="ApplicationUser.Id"/>).</summary>
    [Required]
    [MaxLength(450)]
    public string UserId { get; set; } = string.Empty;

    /// <summary>
    /// Stable identifier for the hint, e.g. <c>wan-filter-compare</c>. Chosen by the caller and
    /// never parsed - renaming one simply starts its count over, which is the harmless outcome.
    /// </summary>
    [Required]
    [MaxLength(100)]
    public string HintKey { get; set; } = string.Empty;

    /// <summary>How many times the hint has been shown to this user.</summary>
    public int TimesShown { get; set; }

    /// <summary>When the count last moved, for diagnosing a hint that will not settle.</summary>
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
