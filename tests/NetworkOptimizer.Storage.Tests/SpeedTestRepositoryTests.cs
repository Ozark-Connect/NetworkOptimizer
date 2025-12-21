using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;
using NetworkOptimizer.Storage.Models;
using NetworkOptimizer.Storage.Repositories;
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
}
