using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;
using NetworkOptimizer.Storage.Models;
using NetworkOptimizer.Storage.Repositories;
using Xunit;

namespace NetworkOptimizer.Storage.Tests;

public class SettingsRepositoryTests : IDisposable
{
    private readonly NetworkOptimizerDbContext _context;
    private readonly SettingsRepository _repository;

    public SettingsRepositoryTests()
    {
        var options = new DbContextOptionsBuilder<NetworkOptimizerDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;

        _context = new NetworkOptimizerDbContext(options);
        var logger = new Mock<ILogger<SettingsRepository>>();
        _repository = new SettingsRepository(_context, logger.Object);
    }

    public void Dispose()
    {
        _context.Dispose();
    }

    #region SystemSettings Tests

    [Fact]
    public async Task GetSystemSettingAsync_ReturnsValue()
    {
        _context.SystemSettings.Add(new SystemSetting { Key = "test-key", Value = "test-value" });
        await _context.SaveChangesAsync();

        var result = await _repository.GetSystemSettingAsync("test-key");

        result.Should().Be("test-value");
    }

    [Fact]
    public async Task GetSystemSettingAsync_ReturnsNullForMissing()
    {
        var result = await _repository.GetSystemSettingAsync("nonexistent");
        result.Should().BeNull();
    }

    [Fact]
    public async Task SaveSystemSettingAsync_CreatesNewSetting()
    {
        await _repository.SaveSystemSettingAsync("new-key", "new-value");

        var saved = await _context.SystemSettings.FindAsync("new-key");
        saved.Should().NotBeNull();
        saved!.Value.Should().Be("new-value");
    }

    [Fact]
    public async Task SaveSystemSettingAsync_UpdatesExistingSetting()
    {
        _context.SystemSettings.Add(new SystemSetting { Key = "existing-key", Value = "old-value" });
        await _context.SaveChangesAsync();

        await _repository.SaveSystemSettingAsync("existing-key", "updated-value");

        var saved = await _context.SystemSettings.FindAsync("existing-key");
        saved!.Value.Should().Be("updated-value");
    }

    #endregion

    #region License Tests

    [Fact]
    public async Task SaveLicenseAsync_SavesAndReturnsId()
    {
        var license = new LicenseInfo
        {
            LicenseKey = "LICENSE-KEY-123",
            LicensedTo = "Test Company",
            IsActive = true,
            ExpirationDate = DateTime.UtcNow.AddYears(1)
        };

        var id = await _repository.SaveLicenseAsync(license);

        id.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task GetLicenseAsync_ReturnsActiveLicense()
    {
        _context.Licenses.AddRange(
            new LicenseInfo { LicenseKey = "OLD-KEY", LicensedTo = "Old", IsActive = false },
            new LicenseInfo { LicenseKey = "NEW-KEY", LicensedTo = "Current", IsActive = true }
        );
        await _context.SaveChangesAsync();

        var result = await _repository.GetLicenseAsync();

        result.Should().NotBeNull();
        result!.LicenseKey.Should().Be("NEW-KEY");
        result.IsActive.Should().BeTrue();
    }

    [Fact]
    public async Task GetLicenseAsync_ReturnsNullWhenNoActiveLicense()
    {
        _context.Licenses.Add(new LicenseInfo
        {
            LicenseKey = "INACTIVE-KEY",
            LicensedTo = "Test",
            IsActive = false
        });
        await _context.SaveChangesAsync();

        var result = await _repository.GetLicenseAsync();

        result.Should().BeNull();
    }

    #endregion
}
