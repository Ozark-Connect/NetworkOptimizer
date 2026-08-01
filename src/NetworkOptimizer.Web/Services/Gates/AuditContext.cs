namespace NetworkOptimizer.Web.Services.Gates;

/// <summary>
/// Scoped, opt-in detail enrichment for the audit envelope written by the gate interceptor (design
/// doc 06). A gated method calls <see cref="SetDetails"/> / <see cref="SetTarget"/> to attach a
/// field-level (secret-redacted) diff or a specific target; the interceptor drains it into the emitted
/// event. The envelope (actor, site, action, outcome) is always uniform; the detail is optional.
/// </summary>
public interface IAuditContext
{
    /// <summary>Attaches a structured, secret-redacted detail object (e.g. a before/after diff).</summary>
    void SetDetails(object details);

    /// <summary>Overrides the target id/name recorded on the event.</summary>
    void SetTarget(string? targetId, string? targetName = null);

    /// <summary>
    /// Records the site the action acted on, for a method that acts on a site other than the one the
    /// caller is standing in and cannot say so with a <see cref="SiteSlugAttribute"/> parameter -
    /// assigning a licence identifies its site by row id, and is reached from a page that only ever
    /// opens on the default site, so the caller's own site is never the right answer.
    /// </summary>
    void SetSite(string? siteSlug);

    /// <summary>Reads and clears the pending detail (called by the interceptor after the method runs).</summary>
    (object? Details, string? TargetId, string? TargetName, string? SiteSlug) Drain();
}

/// <inheritdoc />
public sealed class AuditContext : IAuditContext
{
    private object? _details;
    private string? _targetId;
    private string? _targetName;
    private string? _siteSlug;

    /// <inheritdoc />
    public void SetDetails(object details) => _details = details;

    /// <inheritdoc />
    public void SetTarget(string? targetId, string? targetName = null)
    {
        _targetId = targetId;
        _targetName = targetName;
    }

    /// <inheritdoc />
    public void SetSite(string? siteSlug) => _siteSlug = siteSlug;

    /// <inheritdoc />
    public (object? Details, string? TargetId, string? TargetName, string? SiteSlug) Drain()
    {
        var result = (_details, _targetId, _targetName, _siteSlug);
        _details = null;
        _targetId = null;
        _targetName = null;
        _siteSlug = null;
        return result;
    }
}

/// <summary>Thrown by the gate interceptor when a caller fails an authorization gate.</summary>
public sealed class AuthorizationDeniedException : Exception
{
    public AuthorizationDeniedException(string message) : base(message) { }
}
