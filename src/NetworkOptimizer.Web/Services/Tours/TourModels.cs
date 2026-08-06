using System.Text.Json.Serialization;

namespace NetworkOptimizer.Web.Services.Tours;

/// <summary>
/// A guided tour shipped as a JSON document under wwwroot/data/tours/.
/// Adding a tour for a release is a JSON file and nothing else - no C#, no Razor.
/// </summary>
public class TourDefinition
{
    /// <summary>Tour id, e.g. "2.4.0" for a what's-new tour or "highlights-2.4" for a Highlights revision.</summary>
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    /// <summary>"whats-new" or "highlights".</summary>
    [JsonPropertyName("kind")]
    public string Kind { get; set; } = TourKinds.WhatsNew;

    /// <summary>
    /// Release the tour belongs to, X.Y.Z. Optional in the JSON; defaults to the id,
    /// which is already the version for what's-new tours.
    /// </summary>
    [JsonPropertyName("version")]
    public string? Version { get; set; }

    [JsonPropertyName("title")]
    public string Title { get; set; } = "";

    /// <summary>One-line summary of what the tour covers, shown in the offer modal and the Settings launcher.</summary>
    [JsonPropertyName("summary")]
    public string? Summary { get; set; }

    [JsonPropertyName("steps")]
    public List<TourStep> Steps { get; set; } = new();

    /// <summary>Parsed release version; null when neither Version nor Id parses as X.Y[.Z].</summary>
    [JsonIgnore]
    public Version? ParsedVersion => TourVersions.Parse(Version ?? Id);

    [JsonIgnore]
    public bool IsHighlights => string.Equals(Kind, TourKinds.Highlights, StringComparison.OrdinalIgnoreCase);
}

public class TourStep
{
    /// <summary>
    /// Stable step id, unique across all tours. A step revised in a later release keeps
    /// its id so the newer copy replaces the older one in a merged tour.
    /// </summary>
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    /// <summary>"major", "minor", or "advanced".</summary>
    [JsonPropertyName("level")]
    public string Level { get; set; } = TourLevels.Major;

    /// <summary>
    /// Deep link the driver navigates to before spotlighting the target. Never a synthetic
    /// click. The driver stamps ?site= onto it - step authors must not.
    /// </summary>
    [JsonPropertyName("url")]
    public string Url { get; set; } = "/";

    /// <summary>CSS selector for the spotlight target - always a [data-tour="..."] attribute, never a class.</summary>
    [JsonPropertyName("selector")]
    public string Selector { get; set; } = "";

    [JsonPropertyName("title")]
    public string Title { get; set; } = "";

    /// <summary>
    /// Optional label for the offer modal's step list; falls back to Title. For steps
    /// where the card heading and the list entry want different wording.
    /// </summary>
    [JsonPropertyName("listLabel")]
    public string? ListLabel { get; set; }

    /// <summary>
    /// Keeps this step out of the offer modal's list while still walking the user through it. For
    /// the second half of a feature that takes two stops to show: the pair is one idea, and giving
    /// it two bullets spends twice the space of anything else on the list, which is capped at six.
    /// </summary>
    [JsonPropertyName("hideFromList")]
    public bool HideFromList { get; set; }

    /// <summary>
    /// Narrows the spotlight to the row inside <see cref="Selector"/> whose text contains this,
    /// and scrolls to it. For a list whose rows depend on the user's own configuration: the anchor
    /// can only sit on the list, while the useful target is one row in it. Absent text is not a
    /// failure - the step falls back to spotlighting the anchor itself.
    /// </summary>
    [JsonPropertyName("matchText")]
    public string? MatchText { get; set; }

    [JsonPropertyName("body")]
    public string Body { get; set; } = "";

    /// <summary>Card placement relative to the target: "top", "bottom", "left", "right" or "auto".</summary>
    [JsonPropertyName("placement")]
    public string? Placement { get; set; }

    /// <summary>
    /// "new" (default) or "improved" - shown as a badge on the step card so an
    /// enhancement to an existing feature doesn't present itself as brand new.
    /// </summary>
    [JsonPropertyName("badge")]
    public string? Badge { get; set; }

    /// <summary>Named predicates that must all hold (on at least one visible site) for the step to render.</summary>
    [JsonPropertyName("requires")]
    public List<string> Requires { get; set; } = new();

    /// <summary>
    /// True when the target may legitimately be absent (e.g. no device has rebooted yet).
    /// The driver skips the step without recording it as seen, so it stays eligible.
    /// </summary>
    [JsonPropertyName("optional")]
    public bool Optional { get; set; }

    [JsonIgnore]
    public bool IsImproved => string.Equals(Badge, "improved", StringComparison.OrdinalIgnoreCase);

    [JsonIgnore]
    public bool IsMinor => string.Equals(Level, TourLevels.Minor, StringComparison.OrdinalIgnoreCase);

    [JsonIgnore]
    public bool IsMajor => string.Equals(Level, TourLevels.Major, StringComparison.OrdinalIgnoreCase);
}

public static class TourKinds
{
    public const string WhatsNew = "whats-new";
    public const string Highlights = "highlights";
}

public static class TourLevels
{
    public const string Major = "major";
    public const string Minor = "minor";
    public const string Advanced = "advanced";
}

public static class TourVersions
{
    /// <summary>Parses "X.Y" or "X.Y.Z"; null for anything else (e.g. "highlights-2.4" ids without a version field).</summary>
    public static Version? Parse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        return System.Version.TryParse(value.Trim(), out var v) ? v : null;
    }

    /// <summary>
    /// Tour-scoped source-build test, deliberately NOT on AppVersionInfo so tour
    /// semantics never leak into core versioning. Unlike AppVersionInfo.IsSourceBuild
    /// (0.0.0-base only), this also counts MinVer prerelease height ("2.3.2-alpha.0.12",
    /// a git-checkout build past the last tag) as a source build - such a build must
    /// namespace its recorded state and judge tours by its shipped content, not by a
    /// release that does not exist yet. Build metadata is ignored: the SDK appends
    /// "+sha" to every git build, releases included, so a build exactly on a release
    /// tag reports e.g. "2.3.1+abc123" and still counts as a release here.
    /// </summary>
    public static bool IsSourceBuild =>
        AppVersionInfo.ReleaseVersion is null
        || AppVersionInfo.Informational.Split('+')[0] != AppVersionInfo.ReleaseVersion;
}

/// <summary>A step resolved for playback: site-stamped URL plus its owning tour.</summary>
public class ResolvedTourStep
{
    public required TourStep Step { get; init; }
    public required string TourId { get; init; }
    /// <summary>Step URL with ?site= stamped when multi-site is enabled.</summary>
    public required string NavigateUrl { get; init; }
}

/// <summary>What the offer modal (or a manual launch) will play.</summary>
public class TourOffer
{
    public required string Title { get; init; }
    public string? Summary { get; init; }
    public required bool IsHighlights { get; init; }
    public required List<ResolvedTourStep> Steps { get; init; }
    /// <summary>Ids of every tour folded into this offer, for offer/dismissal records.</summary>
    public required List<string> TourIds { get; init; }
    /// <summary>Steps dropped by the merged-tour cap; the modal says so when non-zero.</summary>
    public int DroppedCount { get; init; }
    /// <summary>True when this is an automatic offer (records an offer when shown); false for manual launches.</summary>
    public bool Automatic { get; init; }
}

public enum TourStatus
{
    NotSeen,
    Deferred,
    Completed,
    Skipped
}

/// <summary>Per-tour state line for the Settings - Application launcher.</summary>
public class TourStatusInfo
{
    public required TourDefinition Tour { get; init; }
    public required TourStatus Status { get; init; }
    /// <summary>Steps currently eligible (level + predicates) for this tour.</summary>
    public required int EligibleStepCount { get; init; }
    public required int SeenStepCount { get; init; }
}
