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
    /// Declares that this call changed nothing, so no event is written. For an idempotent "ensure"
    /// method that found everything already in place: it is reached on ordinary page loads, and a
    /// "changed" entry for a call that changed nothing is both noise and untrue - it buries the real
    /// changes an audit log exists to show. Only ever called on the success path, so a method that
    /// throws is still audited as a failure.
    /// </summary>
    void SuppressNoChange();

    /// <summary>Reads and clears the pending detail (called by the interceptor after the method runs).</summary>
    (object? Details, string? TargetId, string? TargetName, bool Suppressed) Drain();
}

/// <inheritdoc />
public sealed class AuditContext : IAuditContext
{
    private object? _details;
    private string? _targetId;
    private string? _targetName;
    private bool _suppressed;

    /// <inheritdoc />
    public void SetDetails(object details) => _details = details;

    /// <inheritdoc />
    public void SetTarget(string? targetId, string? targetName = null)
    {
        _targetId = targetId;
        _targetName = targetName;
    }

    /// <inheritdoc />
    public void SuppressNoChange() => _suppressed = true;

    /// <inheritdoc />
    public (object? Details, string? TargetId, string? TargetName, bool Suppressed) Drain()
    {
        var result = (_details, _targetId, _targetName, _suppressed);
        _details = null;
        _targetId = null;
        _targetName = null;
        _suppressed = false;
        return result;
    }
}

/// <summary>Thrown by the gate interceptor when a caller fails an authorization gate.</summary>
public sealed class AuthorizationDeniedException : Exception
{
    public AuthorizationDeniedException(string message) : base(message) { }
}
