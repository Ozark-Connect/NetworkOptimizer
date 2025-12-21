using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;
using NetworkOptimizer.Storage.Models;
using NetworkOptimizer.Storage.Repositories;
using Xunit;

namespace NetworkOptimizer.Storage.Tests;

public class SqmRepositoryTests : IDisposable
{
    private readonly NetworkOptimizerDbContext _context;
    private readonly SqmRepository _repository;

    public SqmRepositoryTests()
    {
        var options = new DbContextOptionsBuilder<NetworkOptimizerDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;

        _context = new NetworkOptimizerDbContext(options);
        var logger = new Mock<ILogger<SqmRepository>>();
        _repository = new SqmRepository(_context, logger.Object);
    }

    public void Dispose()
    {
        _context.Dispose();
    }

    [Fact]
    public async Task SaveSqmBaselineAsync_SavesAndReturnsId()
    {
        var baseline = CreateSqmBaseline("gateway-1", "eth0");

        var id = await _repository.SaveSqmBaselineAsync(baseline);

        id.Should().BeGreaterThan(0);
        var saved = await _context.SqmBaselines.FindAsync(id);
        saved.Should().NotBeNull();
    }

    [Fact]
    public async Task GetSqmBaselineAsync_ReturnsCorrectBaseline()
    {
        var baseline = CreateSqmBaseline("gateway-1", "eth0", downloadMbps: 100.0, uploadMbps: 20.0);
        _context.SqmBaselines.Add(baseline);
        await _context.SaveChangesAsync();

        var result = await _repository.GetSqmBaselineAsync("gateway-1", "eth0");

        result.Should().NotBeNull();
        result!.RecommendedDownloadMbps.Should().Be(100.0);
        result.RecommendedUploadMbps.Should().Be(20.0);
    }

    [Fact]
    public async Task GetSqmBaselineAsync_ReturnsNullWhenNotFound()
    {
        var result = await _repository.GetSqmBaselineAsync("nonexistent", "eth0");
        result.Should().BeNull();
    }

    [Fact]
    public async Task GetAllSqmBaselinesAsync_ReturnsAll()
    {
        _context.SqmBaselines.AddRange(
            CreateSqmBaseline("device-1", "eth0"),
            CreateSqmBaseline("device-2", "eth1")
        );
        await _context.SaveChangesAsync();

        var results = await _repository.GetAllSqmBaselinesAsync();

        results.Should().HaveCount(2);
    }

    [Fact]
    public async Task GetAllSqmBaselinesAsync_FiltersByDevice()
    {
        _context.SqmBaselines.AddRange(
            CreateSqmBaseline("device-1", "eth0"),
            CreateSqmBaseline("device-2", "eth1")
        );
        await _context.SaveChangesAsync();

        var results = await _repository.GetAllSqmBaselinesAsync(deviceId: "device-1");

        results.Should().HaveCount(1);
        results[0].DeviceId.Should().Be("device-1");
    }

    [Fact]
    public async Task DeleteSqmBaselineAsync_RemovesBaseline()
    {
        var baseline = CreateSqmBaseline("device-1", "eth0");
        _context.SqmBaselines.Add(baseline);
        await _context.SaveChangesAsync();
        var id = baseline.Id;

        await _repository.DeleteSqmBaselineAsync(id);

        var deleted = await _context.SqmBaselines.FindAsync(id);
        deleted.Should().BeNull();
    }

    private static SqmBaseline CreateSqmBaseline(
        string deviceId,
        string interfaceId,
        double downloadMbps = 100.0,
        double uploadMbps = 20.0)
    {
        return new SqmBaseline
        {
            DeviceId = deviceId,
            InterfaceId = interfaceId,
            InterfaceName = "WAN",
            BaselineStart = DateTime.UtcNow.AddDays(-7),
            BaselineEnd = DateTime.UtcNow,
            RecommendedDownloadMbps = downloadMbps,
            RecommendedUploadMbps = uploadMbps
        };
    }
}
