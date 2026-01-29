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
    /// Marks the current nag level as shown, updating the timestamp.
    /// Call this after displaying the nag to the user.
    /// </summary>
    Task MarkNagShownAsync();

    /// <summary>
    /// Gets the current usage count (audits + speed tests + SQM bonus).
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
