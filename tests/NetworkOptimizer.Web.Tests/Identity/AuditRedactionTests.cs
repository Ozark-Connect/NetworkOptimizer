using FluentAssertions;
using NetworkOptimizer.Web.Services.Auditing;
using Xunit;

namespace NetworkOptimizer.Web.Tests.Identity;

/// <summary>
/// The audit redaction is allowlist-based (design doc 05): only explicitly loggable fields show their
/// values; every other changed field is recorded as the redaction marker, so a secret is never leaked
/// because someone forgot to blocklist it.
/// </summary>
public class AuditRedactionTests
{
    [Fact]
    public void OnlyAllowlistedChangedFields_ShowValues_RestAreRedacted()
    {
        var before = new Dictionary<string, string?> { ["Host"] = "10.0.0.1", ["ClientSecret"] = "old", ["Scopes"] = "openid" };
        var after = new Dictionary<string, string?> { ["Host"] = "10.0.0.2", ["ClientSecret"] = "new", ["Scopes"] = "openid" };
        var loggable = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Host" };

        var diff = AuditRedaction.RedactedDiff(before, after, loggable);

        diff.Should().ContainKey("Host");
        diff.Should().ContainKey("ClientSecret");
        diff["ClientSecret"].Should().Be(AuditRedaction.Redacted, "a changed non-allowlisted field is never shown");
        diff.Should().NotContainKey("Scopes", "unchanged fields are omitted");
    }

    [Fact]
    public void UnchangedFields_AreOmitted()
    {
        var same = new Dictionary<string, string?> { ["A"] = "1", ["B"] = "2" };
        AuditRedaction.RedactedDiff(same, same, new HashSet<string> { "A", "B" }).Should().BeEmpty();
    }
}
