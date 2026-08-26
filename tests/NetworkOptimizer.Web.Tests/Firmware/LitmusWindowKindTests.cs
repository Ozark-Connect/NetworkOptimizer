using FluentAssertions;
using NetworkOptimizer.Core.Helpers;
using NetworkOptimizer.Web.Services.Firmware;
using Xunit;

namespace NetworkOptimizer.Web.Tests.Firmware;

/// <summary>
/// The window a resource capture asks for. Pre-upgrade bounds come from the clock and post-upgrade
/// bounds from the step's own BackAt, which SQLite returns Unspecified - and the Influx client reads
/// Unspecified as local. On a server with a timezone set that asked for hours that had not happened
/// yet, so every post-upgrade reading came back empty and the report printed no CPU or memory.
/// </summary>
public class LitmusWindowKindTests
{
    [Fact]
    public void AsUtc_UnspecifiedFromTheDatabase_IsTakenAsUtc()
    {
        var backAt = new DateTime(2026, 8, 15, 4, 24, 41, DateTimeKind.Unspecified);

        var utc = DateTimeUtilities.AsUtc(backAt);

        utc.Kind.Should().Be(DateTimeKind.Utc);
        utc.Should().Be(new DateTime(2026, 8, 15, 4, 24, 41, DateTimeKind.Utc));
    }

    [Fact]
    public void AsUtc_AlreadyUtc_IsUnchanged()
    {
        var now = new DateTime(2026, 8, 15, 4, 24, 41, DateTimeKind.Utc);

        DateTimeUtilities.AsUtc(now).Should().Be(now);
    }

    [Fact]
    public void AsUtc_Local_IsConverted()
    {
        var local = new DateTime(2026, 8, 15, 4, 24, 41, DateTimeKind.Local);

        DateTimeUtilities.AsUtc(local).Should().Be(local.ToUniversalTime());
    }
}
