namespace NetworkOptimizer.Web.Services.Firmware;

/// <summary>What the public feed knows about one build.</summary>
/// <param name="PublishedAt">Publish date, or null when the feed does not carry this build.</param>
/// <param name="ChangelogUrl">Changelog link, where the build has one.</param>
public sealed record ReleaseMetadata(DateTime? PublishedAt, string? ChangelogUrl);

/// <summary>
/// Publish dates and changelog links per model and version - the two things the console's own
/// catalog cannot answer. Behind an interface because the ripeness gate and the report both read
/// it, and neither should need the network to be tested.
/// </summary>
public interface IReleaseMetadataSource
{
    /// <summary>
    /// What the feed carries for a build, or null when it carries nothing (which includes every
    /// RC and EA build - anonymous feed access serves GA only).
    /// </summary>
    /// <param name="model">Model code as the console reports it.</param>
    /// <param name="version">Firmware version, in either the console's or the feed's spelling.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<ReleaseMetadata?> GetAsync(string? model, string? version, CancellationToken cancellationToken = default);
}

/// <inheritdoc cref="IReleaseMetadataSource" />
public class ReleaseFeedMetadataSource : IReleaseMetadataSource
{
    private readonly UbiquitiReleaseFeedClient _feed;

    /// <param name="feed">Ubiquiti's public release feed.</param>
    public ReleaseFeedMetadataSource(UbiquitiReleaseFeedClient feed) => _feed = feed;

    /// <inheritdoc />
    public async Task<ReleaseMetadata?> GetAsync(
        string? model, string? version, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(model) || string.IsNullOrWhiteSpace(version))
            return null;

        var release = await _feed.GetByVersionAsync(model, version, cancellationToken);
        return release == null ? null : new ReleaseMetadata(release.Created, release.ChangelogUrl);
    }
}

/// <summary>
/// The autopilot release-ripeness rule: a build has to have been published for a while before an
/// unattended rollout will install it. An unknown publish date counts as ripe - a feed outage must
/// never quietly stall autopilot - and the plan says so in its notes.
/// </summary>
public static class ReleaseRipeness
{
    /// <summary>Whether a build may be rolled out now.</summary>
    /// <param name="publishedAt">Publish date, or null when it could not be resolved.</param>
    /// <param name="nowUtc">Current time.</param>
    /// <param name="minAgeDays">Days a build must have been published; 0 or less disables the gate.</param>
    public static bool IsRipe(DateTime? publishedAt, DateTime nowUtc, int minAgeDays)
    {
        if (minAgeDays <= 0) return true;
        if (publishedAt is not DateTime published) return true;
        return nowUtc - DateTime.SpecifyKind(published, DateTimeKind.Utc) >= TimeSpan.FromDays(minAgeDays);
    }

    /// <summary>Whole days since a build was published, or null when the date is unknown.</summary>
    /// <param name="publishedAt">Publish date.</param>
    /// <param name="nowUtc">Current time.</param>
    public static int? AgeDays(DateTime? publishedAt, DateTime nowUtc)
    {
        if (publishedAt is not DateTime published) return null;
        var age = nowUtc - DateTime.SpecifyKind(published, DateTimeKind.Utc);
        return (int)Math.Max(0, Math.Floor(age.TotalDays));
    }
}
