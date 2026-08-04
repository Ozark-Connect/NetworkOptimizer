using FluentAssertions;
using Microsoft.AspNetCore.WebUtilities;
using Xunit;

namespace NetworkOptimizer.Web.Tests.Monitoring;

/// <summary>
/// Clicking a WAN's live score has to open THAT WAN's report. The live tiles and the analysis
/// pages keep their selections apart on purpose, so the link carries the WAN explicitly rather
/// than the two sharing state.
/// </summary>
public class IspHealthWanDeepLinkTests
{
    private static string? LinkedWanKey(string uri)
    {
        var value = QueryHelpers.ParseQuery(new Uri(uri).Query)
            .TryGetValue("wan", out var v) ? v.ToString() : null;
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim().ToLowerInvariant();
    }

    [Theory]
    [InlineData("https://x/monitoring?tab=isp-health&wan=wan2", "wan2")]
    [InlineData("https://x/monitoring?tab=isp-health&wan=WAN2", "wan2")]
    [InlineData("https://x/monitoring?tab=isp-health", null)]
    [InlineData("https://x/monitoring?tab=isp-health&wan=", null)]
    [InlineData("https://x/monitoring", null)]
    public void TheLinkedWanIsReadFromTheQuery(string uri, string? expected)
    {
        LinkedWanKey(uri).Should().Be(expected);
    }

    [Fact]
    public void APrimarySelectionAddsNoParameter()
    {
        // The primary's report is what the page opens on anyway; a parameter would only be noise
        // in the address bar.
        var query = (IsPrimary: true, Key: "wan") is { IsPrimary: false } w
            ? $"&wan={Uri.EscapeDataString(w.Key)}" : "";

        query.Should().BeEmpty();
    }

    [Fact]
    public void ANonPrimarySelectionTravels()
    {
        var sel = (IsPrimary: false, Key: "wan2");
        var query = !sel.IsPrimary ? $"&wan={Uri.EscapeDataString(sel.Key)}" : "";

        query.Should().Be("&wan=wan2");
    }
}
