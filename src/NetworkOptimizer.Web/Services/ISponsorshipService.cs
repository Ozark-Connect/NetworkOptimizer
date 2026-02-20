namespace NetworkOptimizer.Web.Services;

/// <summary>
/// Data returned when a sponsorship nag should be shown
/// </summary>
public record SponsorshipNag(
    int Level,
    string Quip,
    string ActionText,
    string ActionUrl
);

/// <summary>
/// Service for managing sponsorship nag display with tiered messaging
/// </summary>
public interface ISponsorshipService
{
    /// <summary>
    /// Gets the current sponsorship nag to display, if any.
    /// Returns null if no nag should be shown (already shown today, or usage too low).
    /// </summary>
    /// <param name="alwaysShow">When true, returns a nag regardless of daily limit (for Settings page).</param>
    Task<SponsorshipNag?> GetCurrentNagAsync(bool alwaysShow = false);

    /// <summary>
    /// Marks the specified nag level as shown, updating the timestamp.
    /// Call this when displaying a NEW level (after 24h cooldown).
    /// </summary>
    /// <param name="level">The level being shown.</param>
    Task MarkLevelShownAsync(int level);

    /// <summary>
    /// Gets the current usage count (audits + speed tests/2 + signal points/50
    /// + placed APs/2 + floors/2 + SQM bonus).
    /// </summary>
    Task<int> GetUsageCountAsync();

    /// <summary>
    /// Gets the earned sponsorship level based on usage count (1-10).
    /// </summary>
    Task<int> GetEarnedLevelAsync();

    /// <summary>
    /// Marks the user as already being a sponsor, permanently dismissing all nags.
    /// </summary>
    Task MarkAsAlreadySponsorAsync();

    /// <summary>
    /// Checks if the user has marked themselves as already being a sponsor.
    /// </summary>
    Task<bool> IsAlreadySponsorAsync();
}
