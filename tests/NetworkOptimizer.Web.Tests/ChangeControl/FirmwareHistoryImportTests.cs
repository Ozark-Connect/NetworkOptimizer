using FluentAssertions;
using NetworkOptimizer.Storage.Models;
using NetworkOptimizer.Storage.Services;
using NetworkOptimizer.Web.Services.ChangeControl;
using Xunit;

namespace NetworkOptimizer.Web.Tests.ChangeControl;

/// <summary>How past firmware upgrades are found and matched against what is already recorded.</summary>
public sealed class FirmwareHistoryImportTests
{
    private static readonly DateTime T = new(2026, 9, 1, 6, 0, 0, DateTimeKind.Utc);
    private const string Mac = "aa:bb:cc:00:00:01";

    private static MonitoringInfluxClient.DeviceRebootPoint Boot(DateTime at, string category, string? version, int classifier = 8) =>
        new() { DeviceMac = "aabbcc000001", BootedAt = at, Category = category, FirmwareVersion = version, ClassifierVersion = classifier };

    [Fact]
    public void FromBoots_OneUpgradePerBoot_WithThePreviousBootsVersion()
    {
        var boots = new[]
        {
            Boot(T, "PowerLoss", "4.1.13"),
            Boot(T.AddDays(10), "FirmwareUpgrade", "4.3.6", classifier: 7),
            Boot(T.AddDays(10).AddMinutes(1), "FirmwareUpgrade", "4.3.6"),
        };

        var result = FirmwareHistoryImport.FromBoots(boots, new Dictionary<string, string> { [Mac] = "Gateway" });

        result.Should().ContainSingle();
        result[0].Should().BeEquivalentTo(new FirmwareHistoryImport.Upgrade(T.AddDays(10).AddMinutes(1), Mac, "Gateway", "4.1.13", "4.3.6", null));
    }

    [Fact]
    public void FromBoots_UpgradeBootsThatKeptTheirVersion_AreNotUpgrades()
    {
        var boots = new[]
        {
            Boot(T, "FirmwareUpgrade", "5.1.26.33914"),
            Boot(T.AddDays(5), "FirmwareUpgrade", "5.1.26.33914"),
            Boot(T.AddDays(9), "FirmwareUpgrade", "5.1.26"),
            Boot(T.AddDays(12), "FirmwareUpgrade", "5.1.28.34022"),
        };

        FirmwareHistoryImport.FromBoots(boots, new Dictionary<string, string>())
            .Select(u => (u.From, u.To)).Should().Equal((null, "5.1.26.33914"), ("5.1.26.33914", "5.1.28.34022"));
    }

    [Fact]
    public void FromBoots_VersionlessUpgradeBoot_IsJudgedByItsNeighbours()
    {
        var boots = new[]
        {
            Boot(T, "PowerLoss", "6.0.7"),
            Boot(T.AddDays(1), "FirmwareUpgrade", null),
            Boot(T.AddDays(2), "PowerLoss", "6.0.7"),
            Boot(T.AddDays(3), "FirmwareUpgrade", null),
            Boot(T.AddDays(4), "FirmwareUpgrade", "6.0.10"),
            Boot(T.AddDays(5), "FirmwareUpgrade", null),
        };

        FirmwareHistoryImport.FromBoots(boots, new Dictionary<string, string>())
            .Select(u => (u.At, u.To)).Should().Equal((T.AddDays(4), "6.0.10"), (T.AddDays(5), (string?)null));
    }

    [Fact]
    public void FromBoots_DeviceNoLongerOnTheSite_KeepsItsMac()
    {
        var result = FirmwareHistoryImport.FromBoots([Boot(T, "FirmwareUpgrade", "4.3.6")], new Dictionary<string, string>());

        result.Single().Name.Should().Be(Mac);
        result.Single().From.Should().BeNull();
    }

    [Fact]
    public void Merge_RolloutRecordWinsOverTheSameUpgradesReboot()
    {
        var rollout = new FirmwareHistoryImport.Upgrade(T, Mac, "Switch", "7.6.5", "7.6.7.17255", 33);
        var reboot = new FirmwareHistoryImport.Upgrade(T.AddMinutes(-3), Mac, "Switch", "7.6.5", "7.6.7", null);
        var other = new FirmwareHistoryImport.Upgrade(T.AddDays(5), Mac, "Switch", "7.6.7", "7.6.9", null);

        FirmwareHistoryImport.Merge([rollout], [reboot, other]).Should().Equal(rollout, other);
    }

    [Theory]
    [InlineData("Switch: firmware 7.6.5 to 7.6.7.17255", "7.6.7.17255", 2, true)]
    [InlineData("Switch: firmware 7.6.5 to 7.6.7.17255", "7.6.7.17255", 20, false)]
    [InlineData("Switch: firmware 7.6.5 to 7.6.9.1", "7.6.9.1", 1, false)]
    [InlineData("Switch Two: firmware 7.6.5 to 7.6.7.17255", "7.6.7.17255", 1, false)]
    public void IsSame_MatchesDeviceVersionAndTime(string summary, string versionTo, int hoursApart, bool expected)
    {
        var recorded = new ConfigChange { Kind = ConfigChangeKind.DeviceFirmware, OccurredAt = T.AddHours(hoursApart), Summary = summary, VersionTo = versionTo };
        var found = new FirmwareHistoryImport.Upgrade(T, Mac, "Switch", "7.6.5", "7.6.7", null);

        FirmwareHistoryImport.IsSame(recorded, found).Should().Be(expected);
    }

    [Theory]
    [InlineData("aabbcc000001", "aa:bb:cc:00:00:01")]
    [InlineData("AA:BB:CC:00:00:01", "aa:bb:cc:00:00:01")]
    public void Mac_Normalizes(string input, string expected) => FirmwareHistoryImport.Mac(input).Should().Be(expected);

    [Fact]
    public void Summary_SaysWhatIsKnown()
    {
        FirmwareHistoryImport.Summary(new(T, Mac, "AP", "8.8.9", "8.8.11", null)).Should().Be("AP: firmware 8.8.9 to 8.8.11");
        FirmwareHistoryImport.Summary(new(T, Mac, "AP", null, "8.8.11", null)).Should().Be("AP: firmware to 8.8.11");
        FirmwareHistoryImport.Summary(new(T, Mac, "AP", null, null, null)).Should().Be("AP: firmware upgrade");
    }

    [Fact]
    public void Summary_DropsTheBuild() =>
        FirmwareHistoryImport.Summary(new(T, Mac, "AP", "6.0.11.36269", "6.0.15.36489", null)).Should().Be("AP: firmware 6.0.11 to 6.0.15");
}
