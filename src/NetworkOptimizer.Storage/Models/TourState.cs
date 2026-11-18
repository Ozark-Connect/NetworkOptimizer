using System.ComponentModel.DataAnnotations;

namespace NetworkOptimizer.Storage.Models;

/// <summary>
/// Per-subject guided tour state. Lives only in the main (default site) database:
/// a tour is about the product, not a site.
/// Keyed by Subject rather than stored on AdminSettings so that "has this user seen
/// feature X" is per-user by key value alone - the schema stays put. One row per
/// signed-in user; an install with authentication disabled keeps the single
/// <see cref="DefaultSubject"/> row.
/// </summary>
public class TourState
{
    /// <summary>
    /// Subject for an install with authentication disabled, and the key every row used before
    /// per-user state. An install's only account claims the legacy row on first use.
    /// </summary>
    public const string DefaultSubject = "default";

    [Key]
    public int Id { get; set; }

    /// <summary>Who this row is for: the local user id, or <see cref="DefaultSubject"/> when authentication is disabled.</summary>
    [MaxLength(100)]
    public string Subject { get; set; } = DefaultSubject;

    /// <summary>
    /// JSON array of step ids actually rendered to this subject. The source of truth
    /// for "has this subject been shown feature X" - steps dropped by the cap, filtered
    /// by a predicate, or skipped as optional are NOT recorded and stay eligible.
    /// Source builds record ids prefixed with "dev:" so a test site never consumes
    /// the state a release install would read.
    /// </summary>
    public string SeenTourSteps { get; set; } = "[]";

    /// <summary>JSON array of tour ids skipped outright mid-tour, so a skipped tour is not re-offered.</summary>
    public string DismissedTours { get; set; } = "[]";

    /// <summary>
    /// JSON object mapping tour id to the list of app versions in which the tour was
    /// included in an automatic modal offer. Drives both "Later never re-prompts within
    /// a release" and the two-carry limit on deferred tours.
    /// </summary>
    public string TourOffers { get; set; } = "{}";

    /// <summary>Per-subject opt-out ("Don't show again"). No tour is offered automatically while set.</summary>
    public bool ToursDisabled { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
