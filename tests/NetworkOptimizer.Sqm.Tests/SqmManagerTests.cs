using FluentAssertions;
using NetworkOptimizer.Sqm;
using NetworkOptimizer.Sqm.Models;
using Xunit;

namespace NetworkOptimizer.Sqm.Tests;

public class SqmManagerTests
{
    private static SqmConfiguration CreateConfig(
        string iface = "eth2",
        int maxDownloadSpeed = 285,
        int minDownloadSpeed = 190,
        int absoluteMaxDownloadSpeed = 300, // Must be >= maxDownloadSpeed for valid config
        double overheadMultiplier = 1.05,
        string pingHost = "8.8.8.8",
        double baselineLatency = 17.9,
        double latencyThreshold = 2.2,
        double latencyDecrease = 0.97,
        double latencyIncrease = 1.04,
        int pingAdjustmentInterval = 5)
    {
        return new SqmConfiguration
        {
            Interface = iface,
            MaxDownloadSpeed = maxDownloadSpeed,
            MinDownloadSpeed = minDownloadSpeed,
            AbsoluteMaxDownloadSpeed = absoluteMaxDownloadSpeed,
            OverheadMultiplier = overheadMultiplier,
            PingHost = pingHost,
            BaselineLatency = baselineLatency,
            LatencyThreshold = latencyThreshold,
            LatencyDecrease = latencyDecrease,
            LatencyIncrease = latencyIncrease,
            PingAdjustmentInterval = pingAdjustmentInterval
        };
    }

    #region Constructor Tests

    [Fact]
    public void Constructor_InitializesCorrectly()
    {
        // Arrange & Act
        var config = CreateConfig();
        var manager = new SqmManager(config);

        // Assert
        manager.GetStatus().Should().NotBeNull();
        manager.GetStatus().LearningModeActive.Should().BeFalse();
    }

    #endregion

    #region ConfigureSqm Tests

    [Fact]
    public void ConfigureSqm_UpdatesConfiguration()
    {
        // Arrange
        var initialConfig = CreateConfig(iface: "eth2");
        var manager = new SqmManager(initialConfig);
        var newConfig = CreateConfig(
            iface: "eth4",
            maxDownloadSpeed: 500,
            minDownloadSpeed: 200,
            absoluteMaxDownloadSpeed: 550); // Must be >= maxDownloadSpeed

        // Act
        manager.ConfigureSqm(newConfig);

        // Assert - Verify through validation which uses the config
        var errors = manager.ValidateConfiguration();
        errors.Should().BeEmpty();
    }

    #endregion

    #region Learning Mode Tests

    [Fact]
    public void StartLearningMode_ActivatesLearningMode()
    {
        // Arrange
        var manager = new SqmManager(CreateConfig());

        // Act
        manager.StartLearningMode();
        var status = manager.GetStatus();

        // Assert
        status.LearningModeActive.Should().BeTrue();
    }

    [Fact]
    public void StopLearningMode_DeactivatesLearningMode()
    {
        // Arrange
        var manager = new SqmManager(CreateConfig());
        manager.StartLearningMode();

        // Act
        manager.StopLearningMode();
        var status = manager.GetStatus();

        // Assert
        status.LearningModeActive.Should().BeFalse();
        // Note: GetStatus() returns actual baseline progress (0% since no samples collected),
        // not the 100% set by StopLearningMode(). This is intentional - progress reflects
        // actual data collected, not learning mode state.
        status.LearningModeProgress.Should().Be(0);
    }

    [Fact]
    public void IsLearningComplete_NoData_ReturnsFalse()
    {
        // Arrange
        var manager = new SqmManager(CreateConfig());

        // Act
        var isComplete = manager.IsLearningComplete();

        // Assert
        isComplete.Should().BeFalse();
    }

    [Fact]
    public void GetLearningProgress_InitialState_ReturnsZero()
    {
        // Arrange
        var manager = new SqmManager(CreateConfig());

        // Act
        var progress = manager.GetLearningProgress();

        // Assert
        progress.Should().Be(0);
    }

    #endregion

    #region GetStatus Tests

    [Fact]
    public void GetStatus_ReturnsCurrentStatus()
    {
        // Arrange
        var manager = new SqmManager(CreateConfig());

        // Act
        var status = manager.GetStatus();

        // Assert
        status.Should().NotBeNull();
        status.LearningModeActive.Should().BeFalse();
        status.LearningModeProgress.Should().Be(0);
    }

    #endregion

    #region TriggerSpeedtest Tests

    [Fact]
    public async Task TriggerSpeedtest_ValidJson_ProcessesResult()
    {
        // Arrange
        var manager = new SqmManager(CreateConfig(maxDownloadSpeed: 300, overheadMultiplier: 1.0));
        var json = @"{
            ""timestamp"": ""2026-01-13T10:00:00Z"",
            ""ping"": { ""latency"": 15.5 },
            ""download"": { ""bandwidth"": 25000000 },
            ""upload"": { ""bandwidth"": 5000000 }
        }";

        // Act
        var effectiveRate = await manager.TriggerSpeedtest(json);

        // Assert
        effectiveRate.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task TriggerSpeedtest_InvalidJson_ThrowsException()
    {
        // Arrange
        var manager = new SqmManager(CreateConfig());

        // Act
        var act = async () => await manager.TriggerSpeedtest("invalid json");

        // Assert
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("Invalid speedtest result");
    }

    [Fact]
    public async Task TriggerSpeedtest_ZeroBandwidth_ThrowsException()
    {
        // Arrange
        var manager = new SqmManager(CreateConfig());
        var json = @"{
            ""timestamp"": ""2026-01-13T10:00:00Z"",
            ""ping"": { ""latency"": 15.5 },
            ""download"": { ""bandwidth"": 0 },
            ""upload"": { ""bandwidth"": 5000000 }
        }";

        // Act
        var act = async () => await manager.TriggerSpeedtest(json);

        // Assert
        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task TriggerSpeedtest_UpdatesStatus()
    {
        // Arrange
        var manager = new SqmManager(CreateConfig(maxDownloadSpeed: 300, overheadMultiplier: 1.0));
        var json = @"{
            ""timestamp"": ""2026-01-13T10:00:00Z"",
            ""ping"": { ""latency"": 15.5 },
            ""download"": { ""bandwidth"": 25000000 },
            ""upload"": { ""bandwidth"": 5000000 }
        }";

        // Act
        await manager.TriggerSpeedtest(json);
        var status = manager.GetStatus();

        // Assert
        status.LastSpeedtest.Should().Be(200); // 25000000 bytes/sec = 200 Mbps
        status.CurrentRate.Should().BeGreaterThan(0);
        status.LastAdjustmentReason.Should().Contain("Speedtest");
    }

    [Fact]
    public async Task TriggerSpeedtest_InLearningMode_UpdatesBaseline()
    {
        // Arrange
        var manager = new SqmManager(CreateConfig(maxDownloadSpeed: 300, overheadMultiplier: 1.0));
        manager.StartLearningMode();
        var json = @"{
            ""timestamp"": ""2026-01-13T10:00:00Z"",
            ""ping"": { ""latency"": 15.5 },
            ""download"": { ""bandwidth"": 25000000 },
            ""upload"": { ""bandwidth"": 5000000 }
        }";

        // Act
        await manager.TriggerSpeedtest(json);
        var progress = manager.GetLearningProgress();

        // Assert - Should have some progress now
        progress.Should().BeGreaterThan(0);
    }

    #endregion

    #region ApplyRateAdjustment Tests

    [Fact]
    public void ApplyRateAdjustment_HighLatency_DecreasesRate()
    {
        // Arrange
        var config = CreateConfig(baselineLatency: 18, latencyThreshold: 2);
        var manager = new SqmManager(config);
        double currentLatency = 25;
        double currentRate = 280;

        // Act
        var (adjustedRate, reason) = manager.ApplyRateAdjustment(currentLatency, currentRate);

        // Assert
        adjustedRate.Should().BeLessThan(currentRate);
        reason.Should().Contain("High latency");
    }

    [Fact]
    public void ApplyRateAdjustment_UpdatesStatus()
    {
        // Arrange
        var manager = new SqmManager(CreateConfig());
        double currentLatency = 18;
        double currentRate = 250;

        // Act
        manager.ApplyRateAdjustment(currentLatency, currentRate);
        var status = manager.GetStatus();

        // Assert
        status.CurrentLatency.Should().Be(currentLatency);
        status.LastAdjustment.Should().BeCloseTo(DateTime.Now, TimeSpan.FromSeconds(1));
        status.LastAdjustmentReason.Should().NotBeEmpty();
    }

    #endregion

    #region LoadBaseline Tests

    [Fact]
    public void LoadBaseline_UpdatesBaselineCalculator()
    {
        // Arrange
        var manager = new SqmManager(CreateConfig());
        var baseline = new BaselineTable
        {
            Baselines = { ["0_12"] = new HourlyBaseline { Median = 250 } }
        };

        // Act
        manager.LoadBaseline(baseline);
        var loadedBaseline = manager.GetBaselineTable();

        // Assert
        loadedBaseline.Baselines.Should().ContainKey("0_12");
        loadedBaseline.Baselines["0_12"].Median.Should().Be(250);
    }

    #endregion

    #region GetBaselineTable Tests

    [Fact]
    public void GetBaselineTable_ReturnsCurrentTable()
    {
        // Arrange
        var manager = new SqmManager(CreateConfig());

        // Act
        var table = manager.GetBaselineTable();

        // Assert
        table.Should().NotBeNull();
    }

    #endregion

    #region ExportBaselineForScript Tests

    [Fact]
    public void ExportBaselineForScript_ReturnsShellFormat()
    {
        // Arrange
        var manager = new SqmManager(CreateConfig());
        manager.LoadBaseline(new BaselineTable
        {
            Baselines =
            {
                ["0_0"] = new HourlyBaseline { Median = 100 },
                ["1_12"] = new HourlyBaseline { Median = 200 }
            }
        });

        // Act
        var export = manager.ExportBaselineForScript();

        // Assert
        export.Should().HaveCount(2);
        export["0_0"].Should().Be("100");
        export["1_12"].Should().Be("200");
    }

    #endregion

    #region GenerateScripts Tests

    [Fact]
    public void GenerateScripts_ReturnsScriptDictionary()
    {
        // Arrange
        var manager = new SqmManager(CreateConfig());

        // Act
        var scripts = manager.GenerateScripts();

        // Assert
        scripts.Should().NotBeNull();
    }

    [Fact]
    public void GenerateScriptsToDirectory_CreatesDirectory()
    {
        // Arrange
        var manager = new SqmManager(CreateConfig());
        var tempDir = Path.Combine(Path.GetTempPath(), $"sqm_test_{Guid.NewGuid()}");

        try
        {
            // Act
            manager.GenerateScriptsToDirectory(tempDir);

            // Assert
            Directory.Exists(tempDir).Should().BeTrue();
        }
        finally
        {
            // Cleanup
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, true);
        }
    }

    #endregion

    #region GetRateBounds Tests

    [Fact]
    public void GetRateBounds_ReturnsCorrectBounds()
    {
        // Arrange
        var config = CreateConfig(absoluteMaxDownloadSpeed: 300);
        var manager = new SqmManager(config);

        // Act
        var (minRate, optimalRate, maxRate) = manager.GetRateBounds();

        // Assert
        minRate.Should().Be(180);
        optimalRate.Should().Be(282); // 94% of 300
        maxRate.Should().Be(285); // 95% of 300
    }

    #endregion

    #region ValidateConfiguration Tests

    [Fact]
    public void ValidateConfiguration_ValidConfig_ReturnsNoErrors()
    {
        // Arrange
        var manager = new SqmManager(CreateConfig());

        // Act
        var errors = manager.ValidateConfiguration();

        // Assert
        errors.Should().BeEmpty();
    }

    [Fact]
    public void ValidateConfiguration_EmptyInterface_ReturnsError()
    {
        // Arrange
        var manager = new SqmManager(CreateConfig(iface: ""));

        // Act
        var errors = manager.ValidateConfiguration();

        // Assert
        errors.Should().Contain("Interface is required");
    }

    [Fact]
    public void ValidateConfiguration_ZeroMaxDownloadSpeed_ReturnsError()
    {
        // Arrange
        var manager = new SqmManager(CreateConfig(maxDownloadSpeed: 0));

        // Act
        var errors = manager.ValidateConfiguration();

        // Assert
        errors.Should().Contain("MaxDownloadSpeed must be greater than 0");
    }

    [Fact]
    public void ValidateConfiguration_MinGreaterThanMax_ReturnsError()
    {
        // Arrange
        var manager = new SqmManager(CreateConfig(minDownloadSpeed: 300, maxDownloadSpeed: 200));

        // Act
        var errors = manager.ValidateConfiguration();

        // Assert
        errors.Should().Contain("MinDownloadSpeed must be less than or equal to MaxDownloadSpeed");
    }

    [Fact]
    public void ValidateConfiguration_AbsoluteMaxLessThanMax_ReturnsError()
    {
        // Arrange
        var manager = new SqmManager(CreateConfig(maxDownloadSpeed: 300, absoluteMaxDownloadSpeed: 200));

        // Act
        var errors = manager.ValidateConfiguration();

        // Assert
        errors.Should().Contain("AbsoluteMaxDownloadSpeed should be greater than or equal to MaxDownloadSpeed");
    }

    [Fact]
    public void ValidateConfiguration_InvalidOverheadMultiplier_ReturnsError()
    {
        // Arrange
        var manager = new SqmManager(CreateConfig(overheadMultiplier: 1.5));

        // Act
        var errors = manager.ValidateConfiguration();

        // Assert
        errors.Should().Contain("OverheadMultiplier should be between 1.0 and 1.2 (0-20% overhead)");
    }

    [Fact]
    public void ValidateConfiguration_EmptyPingHost_ReturnsError()
    {
        // Arrange
        var manager = new SqmManager(CreateConfig(pingHost: ""));

        // Act
        var errors = manager.ValidateConfiguration();

        // Assert
        errors.Should().Contain("Ping Target Host is required");
    }

    [Fact]
    public void ValidateConfiguration_InvalidBaselineLatency_ReturnsError()
    {
        // Arrange
        var manager = new SqmManager(CreateConfig(baselineLatency: 0));

        // Act
        var errors = manager.ValidateConfiguration();

        // Assert
        errors.Should().Contain("BaselineLatency must be greater than 0");
    }

    [Fact]
    public void ValidateConfiguration_InvalidLatencyThreshold_ReturnsError()
    {
        // Arrange
        var manager = new SqmManager(CreateConfig(latencyThreshold: 0));

        // Act
        var errors = manager.ValidateConfiguration();

        // Assert
        errors.Should().Contain("LatencyThreshold must be greater than 0");
    }

    [Fact]
    public void ValidateConfiguration_InvalidLatencyDecrease_ReturnsError()
    {
        // Arrange
        var manager = new SqmManager(CreateConfig(latencyDecrease: 1.5));

        // Act
        var errors = manager.ValidateConfiguration();

        // Assert
        errors.Should().Contain("LatencyDecrease should be between 0 and 1.0 (e.g., 0.97 for 3% decrease)");
    }

    [Fact]
    public void ValidateConfiguration_InvalidLatencyIncrease_ReturnsError()
    {
        // Arrange
        var manager = new SqmManager(CreateConfig(latencyIncrease: 0.9));

        // Act
        var errors = manager.ValidateConfiguration();

        // Assert
        errors.Should().Contain("LatencyIncrease should be between 1.0 and 1.2 (e.g., 1.04 for 4% increase)");
    }

    [Fact]
    public void ValidateConfiguration_InvalidPingInterval_ReturnsError()
    {
        // Arrange
        var manager = new SqmManager(CreateConfig(pingAdjustmentInterval: 0));

        // Act
        var errors = manager.ValidateConfiguration();

        // Assert
        errors.Should().Contain("PingAdjustmentInterval must be at least 1 minute");
    }

    [Fact]
    public void ValidateConfiguration_MultipleErrors_ReturnsAllErrors()
    {
        // Arrange
        var manager = new SqmManager(CreateConfig(
            iface: "",
            maxDownloadSpeed: 0,
            pingHost: ""));

        // Act
        var errors = manager.ValidateConfiguration();

        // Assert
        errors.Should().HaveCountGreaterThan(2);
    }

    #endregion
}
