using FluentAssertions;
using NetworkOptimizer.Sqm;
using NetworkOptimizer.Sqm.Models;
using Xunit;

namespace NetworkOptimizer.Sqm.Tests;

public class BaselineCalculatorTests
{
    #region AddSample Tests

    [Fact]
    public void AddSample_WithSampleObject_AddsSample()
    {
        // Arrange
        var calculator = new BaselineCalculator();
        var sample = new SpeedtestSample
        {
            Timestamp = DateTime.Now,
            DayOfWeek = 0,
            Hour = 12,
            DownloadSpeed = 100,
            UploadSpeed = 20,
            Latency = 15
        };

        // Act
        calculator.AddSample(sample);
        var baseline = calculator.CalculateBaseline();

        // Assert
        baseline.Baselines.Should().ContainKey("0_12");
    }

    [Fact]
    public void AddSample_WithValues_CreatesSampleCorrectly()
    {
        // Arrange
        var calculator = new BaselineCalculator();

        // Act
        calculator.AddSample(100, 20, 15);
        var baseline = calculator.CalculateBaseline();

        // Assert
        baseline.Baselines.Should().HaveCount(1);
        var hourlyBaseline = baseline.Baselines.Values.First();
        hourlyBaseline.Mean.Should().Be(100);
        hourlyBaseline.SampleCount.Should().Be(1);
    }

    [Fact]
    public void AddSample_MultipleSameHour_AveragesCorrectly()
    {
        // Arrange
        var calculator = new BaselineCalculator();
        var baseTime = new DateTime(2026, 1, 13, 12, 0, 0); // Monday at noon

        // Act
        calculator.AddSample(new SpeedtestSample
        {
            Timestamp = baseTime,
            DayOfWeek = 0,
            Hour = 12,
            DownloadSpeed = 100,
            UploadSpeed = 20,
            Latency = 15
        });
        calculator.AddSample(new SpeedtestSample
        {
            Timestamp = baseTime.AddMinutes(30),
            DayOfWeek = 0,
            Hour = 12,
            DownloadSpeed = 200,
            UploadSpeed = 40,
            Latency = 20
        });

        var baseline = calculator.CalculateBaseline();

        // Assert
        baseline.Baselines.Should().HaveCount(1);
        var hourlyBaseline = baseline.Baselines["0_12"];
        hourlyBaseline.Mean.Should().Be(150); // (100 + 200) / 2
        hourlyBaseline.SampleCount.Should().Be(2);
    }

    #endregion

    #region CalculateBaseline Tests

    [Fact]
    public void CalculateBaseline_NoSamples_ReturnsEmptyBaseline()
    {
        // Arrange
        var calculator = new BaselineCalculator();

        // Act
        var baseline = calculator.CalculateBaseline();

        // Assert
        baseline.Baselines.Should().BeEmpty();
        baseline.IsComplete.Should().BeFalse();
    }

    [Fact]
    public void CalculateBaseline_CalculatesMedian_OddCount()
    {
        // Arrange
        var calculator = new BaselineCalculator();
        calculator.AddSample(new SpeedtestSample { DayOfWeek = 0, Hour = 0, DownloadSpeed = 100, UploadSpeed = 10, Latency = 10, Timestamp = DateTime.Now });
        calculator.AddSample(new SpeedtestSample { DayOfWeek = 0, Hour = 0, DownloadSpeed = 200, UploadSpeed = 20, Latency = 15, Timestamp = DateTime.Now });
        calculator.AddSample(new SpeedtestSample { DayOfWeek = 0, Hour = 0, DownloadSpeed = 300, UploadSpeed = 30, Latency = 20, Timestamp = DateTime.Now });

        // Act
        var baseline = calculator.CalculateBaseline();

        // Assert
        baseline.Baselines["0_0"].Median.Should().Be(200); // Middle value
    }

    [Fact]
    public void CalculateBaseline_CalculatesMedian_EvenCount()
    {
        // Arrange
        var calculator = new BaselineCalculator();
        calculator.AddSample(new SpeedtestSample { DayOfWeek = 0, Hour = 0, DownloadSpeed = 100, UploadSpeed = 10, Latency = 10, Timestamp = DateTime.Now });
        calculator.AddSample(new SpeedtestSample { DayOfWeek = 0, Hour = 0, DownloadSpeed = 200, UploadSpeed = 20, Latency = 15, Timestamp = DateTime.Now });
        calculator.AddSample(new SpeedtestSample { DayOfWeek = 0, Hour = 0, DownloadSpeed = 300, UploadSpeed = 30, Latency = 20, Timestamp = DateTime.Now });
        calculator.AddSample(new SpeedtestSample { DayOfWeek = 0, Hour = 0, DownloadSpeed = 400, UploadSpeed = 40, Latency = 25, Timestamp = DateTime.Now });

        // Act
        var baseline = calculator.CalculateBaseline();

        // Assert
        baseline.Baselines["0_0"].Median.Should().Be(250); // (200 + 300) / 2
    }

    [Fact]
    public void CalculateBaseline_CalculatesMinMax()
    {
        // Arrange
        var calculator = new BaselineCalculator();
        calculator.AddSample(new SpeedtestSample { DayOfWeek = 0, Hour = 0, DownloadSpeed = 100, UploadSpeed = 10, Latency = 10, Timestamp = DateTime.Now });
        calculator.AddSample(new SpeedtestSample { DayOfWeek = 0, Hour = 0, DownloadSpeed = 500, UploadSpeed = 50, Latency = 50, Timestamp = DateTime.Now });
        calculator.AddSample(new SpeedtestSample { DayOfWeek = 0, Hour = 0, DownloadSpeed = 300, UploadSpeed = 30, Latency = 30, Timestamp = DateTime.Now });

        // Act
        var baseline = calculator.CalculateBaseline();

        // Assert
        baseline.Baselines["0_0"].Min.Should().Be(100);
        baseline.Baselines["0_0"].Max.Should().Be(500);
    }

    [Fact]
    public void CalculateBaseline_CalculatesStandardDeviation()
    {
        // Arrange
        var calculator = new BaselineCalculator();
        calculator.AddSample(new SpeedtestSample { DayOfWeek = 0, Hour = 0, DownloadSpeed = 100, UploadSpeed = 10, Latency = 10, Timestamp = DateTime.Now });
        calculator.AddSample(new SpeedtestSample { DayOfWeek = 0, Hour = 0, DownloadSpeed = 100, UploadSpeed = 10, Latency = 10, Timestamp = DateTime.Now });
        calculator.AddSample(new SpeedtestSample { DayOfWeek = 0, Hour = 0, DownloadSpeed = 100, UploadSpeed = 10, Latency = 10, Timestamp = DateTime.Now });

        // Act
        var baseline = calculator.CalculateBaseline();

        // Assert - All same values means zero standard deviation
        baseline.Baselines["0_0"].StdDev.Should().Be(0);
    }

    [Fact]
    public void CalculateBaseline_168Samples_MarksComplete()
    {
        // Arrange
        var calculator = new BaselineCalculator();

        // Add sample for each hour of the week (7 days * 24 hours = 168)
        for (int day = 0; day < 7; day++)
        {
            for (int hour = 0; hour < 24; hour++)
            {
                calculator.AddSample(new SpeedtestSample
                {
                    DayOfWeek = day,
                    Hour = hour,
                    DownloadSpeed = 100 + day + hour,
                    UploadSpeed = 20,
                    Latency = 15,
                    Timestamp = DateTime.Now
                });
            }
        }

        // Act
        var baseline = calculator.CalculateBaseline();

        // Assert
        baseline.Baselines.Should().HaveCount(168);
        baseline.IsComplete.Should().BeTrue();
    }

    #endregion

    #region GetBaselineTable Tests

    [Fact]
    public void GetBaselineTable_ReturnsCurrentTable()
    {
        // Arrange
        var calculator = new BaselineCalculator();
        calculator.AddSample(100, 20, 15);
        calculator.CalculateBaseline();

        // Act
        var table = calculator.GetBaselineTable();

        // Assert
        table.Should().NotBeNull();
        table.Baselines.Should().HaveCount(1);
    }

    #endregion

    #region LoadBaselineTable Tests

    [Fact]
    public void LoadBaselineTable_ReplacesCurrentTable()
    {
        // Arrange
        var calculator = new BaselineCalculator();
        var loadedTable = new BaselineTable
        {
            IsComplete = true,
            Baselines = { ["0_0"] = new HourlyBaseline { Mean = 500 } }
        };

        // Act
        calculator.LoadBaselineTable(loadedTable);
        var result = calculator.GetBaselineTable();

        // Assert
        result.Should().BeSameAs(loadedTable);
        result.Baselines["0_0"].Mean.Should().Be(500);
    }

    #endregion

    #region CalculateBlendedSpeed Tests

    [Fact]
    public void CalculateBlendedSpeed_WithinThreshold_Uses60_40Blend()
    {
        // Arrange
        var calculator = new BaselineCalculator();
        double measuredSpeed = 95; // Within 10% of baseline
        double baselineSpeed = 100;

        // Act
        var result = calculator.CalculateBlendedSpeed(measuredSpeed, baselineSpeed);

        // Assert
        // 60% baseline + 40% measured = (100 * 0.6) + (95 * 0.4) = 60 + 38 = 98
        result.Should().Be(98);
    }

    [Fact]
    public void CalculateBlendedSpeed_BelowThreshold_Uses80_20Blend()
    {
        // Arrange
        var calculator = new BaselineCalculator();
        double measuredSpeed = 80; // More than 10% below baseline
        double baselineSpeed = 100;

        // Act
        var result = calculator.CalculateBlendedSpeed(measuredSpeed, baselineSpeed);

        // Assert
        // 80% baseline + 20% measured = (100 * 0.8) + (80 * 0.2) = 80 + 16 = 96
        result.Should().Be(96);
    }

    [Theory]
    [InlineData(100, 100, 0.1, 100)] // Equal speeds
    [InlineData(110, 100, 0.1, 104)] // Measured above baseline (60/40)
    [InlineData(91, 100, 0.1, 96.4)] // Just above threshold (60/40)
    [InlineData(89, 100, 0.1, 97.8)] // Just below threshold (80/20)
    public void CalculateBlendedSpeed_VariousScenarios(
        double measured, double baseline, double threshold, double expected)
    {
        // Arrange
        var calculator = new BaselineCalculator();

        // Act
        var result = calculator.CalculateBlendedSpeed(measured, baseline, threshold);

        // Assert
        result.Should().BeApproximately(expected, 0.1);
    }

    #endregion

    #region GetLearningProgress Tests

    [Fact]
    public void GetLearningProgress_NoData_ReturnsZero()
    {
        // Arrange
        var calculator = new BaselineCalculator();

        // Act
        var progress = calculator.GetLearningProgress();

        // Assert
        progress.Should().Be(0);
    }

    [Fact]
    public void GetLearningProgress_PartialData_ReturnsPercentage()
    {
        // Arrange
        var calculator = new BaselineCalculator();
        // Add 84 samples (half of 168)
        for (int day = 0; day < 7; day++)
        {
            for (int hour = 0; hour < 12; hour++)
            {
                calculator.AddSample(new SpeedtestSample
                {
                    DayOfWeek = day,
                    Hour = hour,
                    DownloadSpeed = 100,
                    UploadSpeed = 20,
                    Latency = 15,
                    Timestamp = DateTime.Now
                });
            }
        }
        calculator.CalculateBaseline();

        // Act
        var progress = calculator.GetLearningProgress();

        // Assert
        progress.Should().Be(50); // 84 / 168 * 100
    }

    [Fact]
    public void IsLearningComplete_NotComplete_ReturnsFalse()
    {
        // Arrange
        var calculator = new BaselineCalculator();
        calculator.AddSample(100, 20, 15);
        calculator.CalculateBaseline();

        // Act
        var isComplete = calculator.IsLearningComplete();

        // Assert
        isComplete.Should().BeFalse();
    }

    #endregion

    #region GetCurrentBaselineSpeed Tests

    [Fact]
    public void GetCurrentBaselineSpeed_NoBaseline_ReturnsNull()
    {
        // Arrange
        var calculator = new BaselineCalculator();

        // Act
        var speed = calculator.GetCurrentBaselineSpeed();

        // Assert
        speed.Should().BeNull();
    }

    [Fact]
    public void GetBaselineSpeed_SpecificTime_ReturnsCorrectBaseline()
    {
        // Arrange
        var calculator = new BaselineCalculator();
        var testTime = new DateTime(2026, 1, 13, 14, 30, 0); // Tuesday at 2:30 PM
        // January 13, 2026 is a Tuesday. In BaselineTable: Monday = 0, Tuesday = 1
        calculator.LoadBaselineTable(new BaselineTable
        {
            Baselines = { ["1_14"] = new HourlyBaseline { Median = 250, DayOfWeek = 1, Hour = 14 } }
        });

        // Act
        var speed = calculator.GetBaselineSpeed(testTime);

        // Assert
        speed.Should().Be(250);
    }

    #endregion

    #region UpdateHourlyBaseline Tests

    [Fact]
    public void UpdateHourlyBaseline_NewEntry_CreatesBaseline()
    {
        // Arrange
        var calculator = new BaselineCalculator();
        var sample = new SpeedtestSample
        {
            DayOfWeek = 2,
            Hour = 10,
            DownloadSpeed = 100,
            UploadSpeed = 20,
            Latency = 15,
            Timestamp = DateTime.Now
        };

        // Act
        calculator.UpdateHourlyBaseline(sample);
        var table = calculator.GetBaselineTable();

        // Assert
        table.Baselines.Should().ContainKey("2_10");
        table.Baselines["2_10"].Mean.Should().Be(100);
        table.Baselines["2_10"].SampleCount.Should().Be(1);
    }

    [Fact]
    public void UpdateHourlyBaseline_ExistingEntry_UpdatesWithEMA()
    {
        // Arrange
        var calculator = new BaselineCalculator();
        var sample1 = new SpeedtestSample { DayOfWeek = 0, Hour = 0, DownloadSpeed = 100, UploadSpeed = 20, Latency = 15, Timestamp = DateTime.Now };
        var sample2 = new SpeedtestSample { DayOfWeek = 0, Hour = 0, DownloadSpeed = 200, UploadSpeed = 40, Latency = 20, Timestamp = DateTime.Now.AddMinutes(1) };

        // Act
        calculator.UpdateHourlyBaseline(sample1);
        calculator.UpdateHourlyBaseline(sample2);
        var table = calculator.GetBaselineTable();

        // Assert
        // EMA with alpha=0.2: new = 0.2 * 200 + 0.8 * 100 = 40 + 80 = 120
        table.Baselines["0_0"].Mean.Should().Be(120);
        table.Baselines["0_0"].SampleCount.Should().Be(2);
    }

    [Fact]
    public void UpdateHourlyBaseline_UpdatesMinMax()
    {
        // Arrange
        var calculator = new BaselineCalculator();
        calculator.UpdateHourlyBaseline(new SpeedtestSample { DayOfWeek = 0, Hour = 0, DownloadSpeed = 100, UploadSpeed = 20, Latency = 15, Timestamp = DateTime.Now });
        calculator.UpdateHourlyBaseline(new SpeedtestSample { DayOfWeek = 0, Hour = 0, DownloadSpeed = 50, UploadSpeed = 10, Latency = 10, Timestamp = DateTime.Now });
        calculator.UpdateHourlyBaseline(new SpeedtestSample { DayOfWeek = 0, Hour = 0, DownloadSpeed = 200, UploadSpeed = 40, Latency = 20, Timestamp = DateTime.Now });

        // Act
        var table = calculator.GetBaselineTable();

        // Assert
        table.Baselines["0_0"].Min.Should().Be(50);
        table.Baselines["0_0"].Max.Should().Be(200);
    }

    #endregion

    #region ExportToShellFormat Tests

    [Fact]
    public void ExportToShellFormat_ReturnsKeyValuePairs()
    {
        // Arrange
        var calculator = new BaselineCalculator();
        calculator.LoadBaselineTable(new BaselineTable
        {
            Baselines =
            {
                ["0_12"] = new HourlyBaseline { Median = 100.6 },
                ["1_14"] = new HourlyBaseline { Median = 200.4 }
            }
        });

        // Act
        var result = calculator.ExportToShellFormat();

        // Assert
        result.Should().HaveCount(2);
        result["0_12"].Should().Be("101"); // Rounded
        result["1_14"].Should().Be("200"); // Rounded
    }

    #endregion

    #region ImportFromShellFormat Tests

    [Fact]
    public void ImportFromShellFormat_CreatesBaselineTable()
    {
        // Arrange
        var calculator = new BaselineCalculator();
        var shellBaseline = new Dictionary<string, string>
        {
            ["0_0"] = "100",
            ["0_1"] = "200",
            ["1_0"] = "150"
        };

        // Act
        calculator.ImportFromShellFormat(shellBaseline);
        var table = calculator.GetBaselineTable();

        // Assert
        table.Baselines.Should().HaveCount(3);
        table.Baselines["0_0"].Median.Should().Be(100);
        table.Baselines["0_1"].Median.Should().Be(200);
        table.Baselines["1_0"].Median.Should().Be(150);
    }

    [Fact]
    public void ImportFromShellFormat_InvalidKey_SkipsEntry()
    {
        // Arrange
        var calculator = new BaselineCalculator();
        var shellBaseline = new Dictionary<string, string>
        {
            ["0_0"] = "100",
            ["invalid"] = "200",
            ["0_25"] = "150" // Hour 25 is invalid
        };

        // Act
        calculator.ImportFromShellFormat(shellBaseline);
        var table = calculator.GetBaselineTable();

        // Assert
        table.Baselines.Should().HaveCount(2); // Only valid entries
    }

    [Fact]
    public void ImportFromShellFormat_InvalidValue_SkipsEntry()
    {
        // Arrange
        var calculator = new BaselineCalculator();
        var shellBaseline = new Dictionary<string, string>
        {
            ["0_0"] = "100",
            ["0_1"] = "not-a-number"
        };

        // Act
        calculator.ImportFromShellFormat(shellBaseline);
        var table = calculator.GetBaselineTable();

        // Assert
        table.Baselines.Should().HaveCount(1);
    }

    #endregion
}
