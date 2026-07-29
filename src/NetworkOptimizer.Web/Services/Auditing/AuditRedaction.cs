namespace NetworkOptimizer.Web.Services.Auditing;

/// <summary>
/// Builds secret-safe, field-level before/after diffs for audit details (design doc 05). Uses an
/// ALLOWLIST: a changed field is shown only if the caller explicitly lists it as loggable; every other
/// changed field is recorded as <c>***changed***</c> so a value is never leaked because someone forgot
/// to blocklist it. Passwords, tokens, secrets, community strings, and agent keys are never loggable.
/// </summary>
public static class AuditRedaction
{
    /// <summary>Marker recorded in place of a changed value that is not on the loggable allowlist.</summary>
    public const string Redacted = "***changed***";

    /// <summary>
    /// Produces a diff of the changed fields between <paramref name="before"/> and <paramref name="after"/>.
    /// Each entry is either <c>{ from, to }</c> (field is in <paramref name="loggableFields"/>) or the
    /// <see cref="Redacted"/> marker. Unchanged fields are omitted.
    /// </summary>
    public static Dictionary<string, object> RedactedDiff(
        IReadOnlyDictionary<string, string?> before,
        IReadOnlyDictionary<string, string?> after,
        ISet<string> loggableFields)
    {
        var diff = new Dictionary<string, object>();
        var keys = new HashSet<string>(before.Keys, StringComparer.OrdinalIgnoreCase);
        keys.UnionWith(after.Keys);

        foreach (var key in keys)
        {
            before.TryGetValue(key, out var from);
            after.TryGetValue(key, out var to);
            if (string.Equals(from, to, StringComparison.Ordinal))
                continue;

            diff[key] = loggableFields.Contains(key)
                ? new { from, to }
                : Redacted;
        }

        return diff;
    }
}
