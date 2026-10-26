using FluentAssertions;
using NetworkOptimizer.Web.Services;
using Xunit;

namespace NetworkOptimizer.Web.Tests.Services;

public class AlertSourceDisplayTests
{
    [Theory]
    [InlineData("rollout", "Firmware Rollout")]
    [InlineData("console", "UniFi Console")]
    [InlineData("cable_modem", "Cable Modem")]
    [InlineData("change_control", "Change Control")]
    [InlineData("speedtest", "Speed Test")]
    [InlineData("wifi", "Wi-Fi")]
    public void KnownSource_ShowsItsDisplayName(string source, string expected)
    {
        AlertSourceDisplay.GetName(source).Should().Be(expected);
    }

    public static TheoryData<string> RuleSources()
    {
        var data = new TheoryData<string>();
        foreach (var source in NetworkOptimizer.Alerts.DefaultAlertRules.GetDefaults()
                     .Select(r => r.Source).OfType<string>().Where(s => s.Length > 0).Distinct().Order())
            data.Add(source);
        return data;
    }

    // A new alert source fails here until it has a display name and a History filter option.
    [Theory]
    [MemberData(nameof(RuleSources))]
    public void EveryDefaultRuleSource_HasADisplayName(string source)
    {
        AlertSourceDisplay.GetName(source).Should().NotBe(source, "every alert source needs a display name");
    }

    [Theory]
    [MemberData(nameof(RuleSources))]
    public void EveryDefaultRuleSource_IsInTheHistoryFilter(string source)
    {
        var page = File.ReadAllText(Path.Combine(WebProjectDir(), "Components", "Pages", "Alerts.razor"));
        page.Should().Contain($"<option value=\"{source}\">{AlertSourceDisplay.GetName(source)}</option>");
    }

    private static string WebProjectDir()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "src", "NetworkOptimizer.Web");
            if (Directory.Exists(candidate)) return candidate;
        }
        throw new DirectoryNotFoundException("src/NetworkOptimizer.Web not found above the test output");
    }

    [Fact]
    public void UnknownSource_ShowsAsStored()
    {
        AlertSourceDisplay.GetName("newsource").Should().Be("newsource");
    }

    [Fact]
    public void NullSource_ShowsNothing()
    {
        AlertSourceDisplay.GetName(null).Should().BeEmpty();
    }

    [Theory]
    [InlineData("agent", "Agent")]
    [InlineData("agent", " agent ")]
    [InlineData("device", "")]
    [InlineData("device", null)]
    public void DeviceName_RepeatingSourceOrEmpty_IsDropped(string source, string? deviceName)
    {
        AlertSourceDisplay.GetDeviceName(source, deviceName).Should().BeNull();
    }

    [Theory]
    [InlineData("agent", "Agent 2")]
    [InlineData("device", "Office Switch")]
    public void DeviceName_Distinct_IsKept(string source, string deviceName)
    {
        AlertSourceDisplay.GetDeviceName(source, deviceName).Should().Be(deviceName);
    }
}
