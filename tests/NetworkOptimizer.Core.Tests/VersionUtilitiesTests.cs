using FluentAssertions;
using NetworkOptimizer.Core.Helpers;
using Xunit;

namespace NetworkOptimizer.Core.Tests;

public class VersionUtilitiesTests
{
    [Theory]
    // Older prerelease of the same core
    [InlineData("2.0.0-beta.1", "2.0.0-beta.2", true)]
    [InlineData("2.0.0-beta.1.9", "2.0.0-beta.2", true)]
    // At or past the required release (MinVer height and build metadata)
    [InlineData("2.0.0-beta.2", "2.0.0-beta.2", false)]
    [InlineData("2.0.0-beta.2.14+3449fbae", "2.0.0-beta.2", false)]
    [InlineData("2.0.0-beta.3", "2.0.0-beta.2", false)]
    // Release outranks prerelease of the same core, and vice versa
    [InlineData("2.0.0", "2.0.0-beta.2", false)]
    [InlineData("2.0.0-beta.2", "2.0.0", true)]
    // Core comparison
    [InlineData("1.9.9", "2.0.0-beta.2", true)]
    [InlineData("2.0.1", "2.0.0", false)]
    [InlineData("2.0.0", "2.0.1", true)]
    // Leading v prefix tolerated
    [InlineData("v2.0.0-beta.1", "2.0.0-beta.2", true)]
    // Prerelease-length asymmetry with a matching prefix: shorter is older
    [InlineData("2.0.0-beta.2", "2.0.0-beta.2.0", true)]
    [InlineData("2.0.0-beta.2.0", "2.0.0-beta.2", false)]
    // Two-part cores normalize to X.Y.0 rather than ranking below X.Y.0
    [InlineData("2.0-beta.2", "2.0.0-beta.2", false)]
    [InlineData("2.0", "2.0.0", false)]
    [InlineData("2.0", "2.0.1", true)]
    public void IsOlderThan_ComparesSemVerPrecedence(string candidate, string required, bool expected)
    {
        VersionUtilities.IsOlderThan(candidate, required).Should().Be(expected);
    }

    [Theory]
    // Missing or unparseable versions never flag
    [InlineData(null, "2.0.0-beta.2")]
    [InlineData("", "2.0.0-beta.2")]
    [InlineData("dev", "2.0.0-beta.2")]
    [InlineData("2.0.0-beta.1", null)]
    [InlineData("2.0.0-beta.1", "not-a-version")]
    public void IsOlderThan_UnparseableInput_ReturnsFalse(string? candidate, string? required)
    {
        VersionUtilities.IsOlderThan(candidate, required).Should().BeFalse();
    }

    [Theory]
    [InlineData("2.0.1+ed2714ca72415f2d08512e643c", "2.0.1")]
    [InlineData("2.0.0-beta.2.14+3449fbae", "2.0.0-beta.2.14")]
    [InlineData("2.0.1", "2.0.1")]
    [InlineData("", "")]
    [InlineData(null, null)]
    public void StripBuildMetadata_DropsPlusSuffixOnly(string? version, string? expected)
    {
        VersionUtilities.StripBuildMetadata(version).Should().Be(expected);
    }
}
