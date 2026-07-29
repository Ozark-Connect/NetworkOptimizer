using System.Text.Json;
using NetworkOptimizer.Storage.Models.Identity;
using NetworkOptimizer.Web.Services.Identity;

namespace NetworkOptimizer.Web.Services.Auditing;

/// <summary>
/// Non-blocking audit sink (design doc 05). Callers never block on audit I/O: <see cref="Log"/>
/// enqueues onto a bounded channel drained by a background writer. Events are constructed with secrets
/// already redacted (allowlist at the call site) - the logger never sees plaintext secrets.
/// </summary>
public interface IAuditLogger
{
    /// <summary>Enqueues an audit event for background persistence. Never throws, never blocks.</summary>
    void Log(AuditEvent auditEvent);
}

/// <summary>
/// Fluent construction of <see cref="AuditEvent"/>s with the actor/source/correlation stamped from the
/// ambient <see cref="ICallerContext"/>, so every emitter produces a uniform envelope and only supplies
/// the action-specific fields.
/// </summary>
public static class AuditEventBuilder
{
    /// <summary>Builds an event for the given category/action, filling actor fields from the caller.</summary>
    public static AuditEvent From(
        CallerInfo? caller,
        string category,
        string action,
        string outcome = AuditOutcomes.Success,
        string? targetType = null,
        string? targetId = null,
        string? targetName = null,
        string? siteSlug = null,
        object? details = null)
        => new()
        {
            TimestampUtc = DateTime.UtcNow,
            ActorUserId = caller?.UserId,
            ActorName = caller?.ActorName,
            ActorAuthMethod = caller?.AuthMethod,
            SourceIp = caller?.SourceIp,
            UserAgent = caller?.UserAgent,
            CorrelationId = caller?.CorrelationId,
            Category = category,
            Action = action,
            Outcome = outcome,
            TargetType = targetType,
            TargetId = targetId,
            TargetName = targetName,
            SiteSlug = siteSlug,
            DetailsJson = details is null ? null : JsonSerializer.Serialize(details),
        };
}
