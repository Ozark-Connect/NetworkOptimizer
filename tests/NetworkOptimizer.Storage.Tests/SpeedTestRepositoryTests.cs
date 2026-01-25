using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;
using NetworkOptimizer.Storage.Models;
using NetworkOptimizer.Storage.Repositories;
using NetworkOptimizer.UniFi.Models;
using Xunit;

namespace NetworkOptimizer.Storage.Tests;

public class SpeedTestRepositoryTests : IDisposable
{
    private readonly NetworkOptimizerDbContext _context;
    private readonly SpeedTestRepository _repository;

    public SpeedTestRepositoryTests()
    {
        var options = new DbContextOptionsBuilder<NetworkOptimizerDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;

        _context = new NetworkOptimizerDbContext(options);
        var logger = new Mock<ILogger<SpeedTestRepository>>();
        _repository = new SpeedTestRepository(_context, logger.Object);
    }

    public void Dispose()
    {
        _context.Dispose();
    }

    #region GatewaySshSettings Tests

    [Fact]
    public async Task GetGatewaySshSettingsAsync_ReturnsSettings()
    {
        _context.GatewaySshSettings.Add(new GatewaySshSettings
        {
            Host = "192.168.1.1",
            Username = "root",
            Port = 22
        });
        await _context.SaveChangesAsync();

        var result = await _repository.GetGatewaySshSettingsAsync();

        result.Should().NotBeNull();
        result!.Host.Should().Be("192.168.1.1");
    }

    [Fact]
    public async Task SaveGatewaySshSettingsAsync_UpdatesExisting()
    {
        _context.GatewaySshSettings.Add(new GatewaySshSettings { Host = "old-host", Username = "root" });
        await _context.SaveChangesAsync();

        var updated = new GatewaySshSettings { Host = "new-host", Username = "admin", Port = 2222 };

        await _repository.SaveGatewaySshSettingsAsync(updated);

        var count = await _context.GatewaySshSettings.CountAsync();
        count.Should().Be(1);
        var saved = await _context.GatewaySshSettings.FirstAsync();
        saved.Host.Should().Be("new-host");
        saved.Username.Should().Be("admin");
    }

    #endregion

    #region Iperf3Result Tests

    [Fact]
    public async Task SaveIperf3ResultAsync_SetsTestTime()
    {
        var result = new Iperf3Result
        {
            DeviceHost = "192.168.1.1",
            DeviceName = "Test Device",
            Success = true
        };

        await _repository.SaveIperf3ResultAsync(result);

        var saved = await _context.Iperf3Results.FirstOrDefaultAsync();
        saved.Should().NotBeNull();
        saved!.TestTime.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task GetRecentIperf3ResultsAsync_ReturnsOrderedByTimeDesc()
    {
        _context.Iperf3Results.AddRange(
            new Iperf3Result { DeviceHost = "host-1", DeviceName = "Device 1", TestTime = DateTime.UtcNow.AddMinutes(-10) },
            new Iperf3Result { DeviceHost = "host-2", DeviceName = "Device 2", TestTime = DateTime.UtcNow },
            new Iperf3Result { DeviceHost = "host-3", DeviceName = "Device 3", TestTime = DateTime.UtcNow.AddMinutes(-5) }
        );
        await _context.SaveChangesAsync();

        var results = await _repository.GetRecentIperf3ResultsAsync(10);

        results.Should().HaveCount(3);
        results[0].DeviceHost.Should().Be("host-2");
        results[1].DeviceHost.Should().Be("host-3");
        results[2].DeviceHost.Should().Be("host-1");
    }

    [Fact]
    public async Task GetRecentIperf3ResultsAsync_RespectsCount()
    {
        for (int i = 0; i < 10; i++)
        {
            _context.Iperf3Results.Add(new Iperf3Result
            {
                DeviceHost = $"host-{i}",
                DeviceName = $"Device {i}",
                TestTime = DateTime.UtcNow.AddMinutes(-i)
            });
        }
        await _context.SaveChangesAsync();

        var results = await _repository.GetRecentIperf3ResultsAsync(5);

        results.Should().HaveCount(5);
    }

    [Fact]
    public async Task GetIperf3ResultsForDeviceAsync_FiltersCorrectly()
    {
        _context.Iperf3Results.AddRange(
            new Iperf3Result { DeviceHost = "host-1", DeviceName = "Device 1", TestTime = DateTime.UtcNow },
            new Iperf3Result { DeviceHost = "host-2", DeviceName = "Device 2", TestTime = DateTime.UtcNow },
            new Iperf3Result { DeviceHost = "host-1", DeviceName = "Device 1", TestTime = DateTime.UtcNow.AddMinutes(-5) }
        );
        await _context.SaveChangesAsync();

        var results = await _repository.GetIperf3ResultsForDeviceAsync("host-1");

        results.Should().HaveCount(2);
        results.Should().AllSatisfy(r => r.DeviceHost.Should().Be("host-1"));
    }

    [Fact]
    public async Task ClearIperf3HistoryAsync_RemovesAllResults()
    {
        _context.Iperf3Results.AddRange(
            new Iperf3Result { DeviceHost = "host-1", DeviceName = "Device 1", TestTime = DateTime.UtcNow },
            new Iperf3Result { DeviceHost = "host-2", DeviceName = "Device 2", TestTime = DateTime.UtcNow }
        );
        await _context.SaveChangesAsync();

        await _repository.ClearIperf3HistoryAsync();

        var remaining = await _context.Iperf3Results.CountAsync();
        remaining.Should().Be(0);
    }

    #endregion

    #region Search Tests

    // NOTE: These tests verify the in-memory filtering approach.
    // When migrating to SQL-side JSON filtering, ensure these tests still pass
    // to maintain behavioral compatibility.

    [Fact]
    public async Task SearchIperf3ResultsAsync_EmptyFilter_ReturnsAll()
    {
        _context.Iperf3Results.AddRange(
            new Iperf3Result { DeviceHost = "192.168.1.10", DeviceName = "AP-Office", TestTime = DateTime.UtcNow },
            new Iperf3Result { DeviceHost = "192.168.1.20", DeviceName = "Switch-Core", TestTime = DateTime.UtcNow.AddMinutes(-5) }
        );
        await _context.SaveChangesAsync();

        var results = await _repository.SearchIperf3ResultsAsync("", count: 50);

        results.Should().HaveCount(2);
    }

    [Fact]
    public async Task SearchIperf3ResultsAsync_FiltersByDeviceHost()
    {
        _context.Iperf3Results.AddRange(
            new Iperf3Result { DeviceHost = "192.168.1.10", DeviceName = "AP-Office", TestTime = DateTime.UtcNow },
            new Iperf3Result { DeviceHost = "192.168.1.20", DeviceName = "Switch-Core", TestTime = DateTime.UtcNow },
            new Iperf3Result { DeviceHost = "10.0.0.5", DeviceName = "AP-Remote", TestTime = DateTime.UtcNow }
        );
        await _context.SaveChangesAsync();

        var results = await _repository.SearchIperf3ResultsAsync("192.168.1");

        results.Should().HaveCount(2);
        results.Should().AllSatisfy(r => r.DeviceHost.Should().Contain("192.168.1"));
    }

    [Fact]
    public async Task SearchIperf3ResultsAsync_FiltersByDeviceName()
    {
        _context.Iperf3Results.AddRange(
            new Iperf3Result { DeviceHost = "192.168.1.10", DeviceName = "AP-Office", TestTime = DateTime.UtcNow },
            new Iperf3Result { DeviceHost = "192.168.1.20", DeviceName = "Switch-Core", TestTime = DateTime.UtcNow },
            new Iperf3Result { DeviceHost = "192.168.1.30", DeviceName = "AP-Warehouse", TestTime = DateTime.UtcNow }
        );
        await _context.SaveChangesAsync();

        var results = await _repository.SearchIperf3ResultsAsync("AP-");

        results.Should().HaveCount(2);
        results.Should().AllSatisfy(r => r.DeviceName.Should().StartWith("AP-"));
    }

    [Fact]
    public async Task SearchIperf3ResultsAsync_FiltersByClientMac()
    {
        _context.Iperf3Results.AddRange(
            new Iperf3Result { DeviceHost = "192.168.1.10", ClientMac = "aa:bb:cc:dd:ee:ff", TestTime = DateTime.UtcNow },
            new Iperf3Result { DeviceHost = "192.168.1.20", ClientMac = "11:22:33:44:55:66", TestTime = DateTime.UtcNow },
            new Iperf3Result { DeviceHost = "192.168.1.30", ClientMac = null, TestTime = DateTime.UtcNow }
        );
        await _context.SaveChangesAsync();

        var results = await _repository.SearchIperf3ResultsAsync("aa:bb:cc");

        results.Should().HaveCount(1);
        results[0].ClientMac.Should().Be("aa:bb:cc:dd:ee:ff");
    }

    [Fact]
    public async Task SearchIperf3ResultsAsync_FiltersByHopInPath()
    {
        // Create a result with path analysis containing a specific switch
        var pathAnalysis = new PathAnalysisResult
        {
            Path = new NetworkPath
            {
                IsValid = true,
                Hops = new List<NetworkHop>
                {
                    new() { DeviceName = "Server", DeviceMac = "00:00:00:00:00:01", DeviceIp = "192.168.1.1" },
                    new() { DeviceName = "Core-Switch", DeviceMac = "00:11:22:33:44:55", DeviceIp = "192.168.1.2" },
                    new() { DeviceName = "AP-Office", DeviceMac = "aa:bb:cc:dd:ee:ff", DeviceIp = "192.168.1.10" }
                }
            }
        };

        _context.Iperf3Results.AddRange(
            new Iperf3Result
            {
                DeviceHost = "192.168.1.10",
                DeviceName = "AP-Office",
                PathAnalysisJson = JsonSerializer.Serialize(pathAnalysis),
                TestTime = DateTime.UtcNow
            },
            new Iperf3Result { DeviceHost = "192.168.1.20", DeviceName = "Switch-Remote", TestTime = DateTime.UtcNow }
        );
        await _context.SaveChangesAsync();

        // Search by hop name that only appears in the path, not in DeviceName
        var results = await _repository.SearchIperf3ResultsAsync("Core-Switch");

        results.Should().HaveCount(1);
        results[0].DeviceName.Should().Be("AP-Office");
    }

    [Fact]
    public async Task SearchIperf3ResultsAsync_FiltersByHopMac()
    {
        var pathAnalysis = new PathAnalysisResult
        {
            Path = new NetworkPath
            {
                IsValid = true,
                Hops = new List<NetworkHop>
                {
                    new() { DeviceName = "Server", DeviceMac = "00:00:00:00:00:01" },
                    new() { DeviceName = "Switch", DeviceMac = "de:ad:be:ef:ca:fe" }
                }
            }
        };

        _context.Iperf3Results.AddRange(
            new Iperf3Result
            {
                DeviceHost = "192.168.1.10",
                PathAnalysisJson = JsonSerializer.Serialize(pathAnalysis),
                TestTime = DateTime.UtcNow
            },
            new Iperf3Result { DeviceHost = "192.168.1.20", TestTime = DateTime.UtcNow }
        );
        await _context.SaveChangesAsync();

        var results = await _repository.SearchIperf3ResultsAsync("de:ad:be");

        results.Should().HaveCount(1);
        results[0].DeviceHost.Should().Be("192.168.1.10");
    }

    [Fact]
    public async Task SearchIperf3ResultsAsync_IsCaseInsensitive()
    {
        _context.Iperf3Results.AddRange(
            new Iperf3Result { DeviceHost = "192.168.1.10", DeviceName = "AP-Office", TestTime = DateTime.UtcNow },
            new Iperf3Result { DeviceHost = "192.168.1.20", DeviceName = "ap-warehouse", TestTime = DateTime.UtcNow }
        );
        await _context.SaveChangesAsync();

        var results = await _repository.SearchIperf3ResultsAsync("AP-");

        results.Should().HaveCount(2);
    }

    [Fact]
    public async Task SearchIperf3ResultsAsync_NoMatch_ReturnsEmpty()
    {
        _context.Iperf3Results.AddRange(
            new Iperf3Result { DeviceHost = "192.168.1.10", DeviceName = "AP-Office", TestTime = DateTime.UtcNow },
            new Iperf3Result { DeviceHost = "192.168.1.20", DeviceName = "Switch-Core", TestTime = DateTime.UtcNow }
        );
        await _context.SaveChangesAsync();

        var results = await _repository.SearchIperf3ResultsAsync("nonexistent");

        results.Should().BeEmpty();
    }

    [Fact]
    public async Task SearchIperf3ResultsAsync_RespectsCountLimit()
    {
        for (int i = 0; i < 10; i++)
        {
            _context.Iperf3Results.Add(new Iperf3Result
            {
                DeviceHost = $"192.168.1.{i}",
                DeviceName = "AP-Test",
                TestTime = DateTime.UtcNow.AddMinutes(-i)
            });
        }
        await _context.SaveChangesAsync();

        var results = await _repository.SearchIperf3ResultsAsync("AP-Test", count: 3);

        results.Should().HaveCount(3);
    }

    [Fact]
    public async Task SearchIperf3ResultsAsync_RespectsHoursFilter()
    {
        _context.Iperf3Results.AddRange(
            new Iperf3Result { DeviceHost = "192.168.1.10", DeviceName = "AP-Recent", TestTime = DateTime.UtcNow },
            new Iperf3Result { DeviceHost = "192.168.1.20", DeviceName = "AP-Old", TestTime = DateTime.UtcNow.AddHours(-25) }
        );
        await _context.SaveChangesAsync();

        var results = await _repository.SearchIperf3ResultsAsync("AP-", count: 50, hours: 24);

        results.Should().HaveCount(1);
        results[0].DeviceName.Should().Be("AP-Recent");
    }

    [Fact]
    public async Task SearchIperf3ResultsAsync_ReturnsOrderedByTimeDesc()
    {
        _context.Iperf3Results.AddRange(
            new Iperf3Result { DeviceHost = "192.168.1.10", DeviceName = "AP-1", TestTime = DateTime.UtcNow.AddMinutes(-10) },
            new Iperf3Result { DeviceHost = "192.168.1.20", DeviceName = "AP-2", TestTime = DateTime.UtcNow },
            new Iperf3Result { DeviceHost = "192.168.1.30", DeviceName = "AP-3", TestTime = DateTime.UtcNow.AddMinutes(-5) }
        );
        await _context.SaveChangesAsync();

        var results = await _repository.SearchIperf3ResultsAsync("AP-");

        results.Should().HaveCount(3);
        results[0].DeviceName.Should().Be("AP-2");  // Most recent
        results[1].DeviceName.Should().Be("AP-3");
        results[2].DeviceName.Should().Be("AP-1");  // Oldest
    }

    #endregion
}
