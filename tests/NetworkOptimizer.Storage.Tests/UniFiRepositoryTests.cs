using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;
using NetworkOptimizer.Storage.Models;
using NetworkOptimizer.Storage.Repositories;
using Xunit;

namespace NetworkOptimizer.Storage.Tests;

public class UniFiRepositoryTests : IDisposable
{
    private readonly NetworkOptimizerDbContext _context;
    private readonly UniFiRepository _repository;

    public UniFiRepositoryTests()
    {
        var options = new DbContextOptionsBuilder<NetworkOptimizerDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;

        _context = new NetworkOptimizerDbContext(options);
        var logger = new Mock<ILogger<UniFiRepository>>();
        _repository = new UniFiRepository(_context, logger.Object);
    }

    public void Dispose()
    {
        _context.Dispose();
    }

    #region UniFiConnectionSettings Tests

    [Fact]
    public async Task GetUniFiConnectionSettingsAsync_ReturnsSettings()
    {
        _context.UniFiConnectionSettings.Add(new UniFiConnectionSettings
        {
            SiteId = 1,
            ControllerUrl = "https://unifi.local",
            Username = "admin",
            UniFiSiteId = "default"
        });
        await _context.SaveChangesAsync();

        var result = await _repository.GetUniFiConnectionSettingsAsync(1);

        result.Should().NotBeNull();
        result!.ControllerUrl.Should().Be("https://unifi.local");
    }

    [Fact]
    public async Task GetUniFiConnectionSettingsAsync_ReturnsNullWhenEmpty()
    {
        var result = await _repository.GetUniFiConnectionSettingsAsync(1);
        result.Should().BeNull();
    }

    [Fact]
    public async Task SaveUniFiConnectionSettingsAsync_CreatesSettings()
    {
        var settings = new UniFiConnectionSettings
        {
            SiteId = 1,
            ControllerUrl = "https://new-unifi.local",
            Username = "admin",
            UniFiSiteId = "default"
        };

        await _repository.SaveUniFiConnectionSettingsAsync(1, settings);

        var saved = await _context.UniFiConnectionSettings.FirstOrDefaultAsync();
        saved.Should().NotBeNull();
        saved!.ControllerUrl.Should().Be("https://new-unifi.local");
    }

    [Fact]
    public async Task SaveUniFiConnectionSettingsAsync_UpdatesExisting()
    {
        _context.UniFiConnectionSettings.Add(new UniFiConnectionSettings
        {
            SiteId = 1,
            ControllerUrl = "https://old.local",
            Username = "old-admin"
        });
        await _context.SaveChangesAsync();

        var updated = new UniFiConnectionSettings
        {
            SiteId = 1,
            ControllerUrl = "https://new.local",
            Username = "new-admin"
        };

        await _repository.SaveUniFiConnectionSettingsAsync(1, updated);

        var count = await _context.UniFiConnectionSettings.CountAsync();
        count.Should().Be(1);
        var saved = await _context.UniFiConnectionSettings.FirstAsync();
        saved.ControllerUrl.Should().Be("https://new.local");
    }

    #endregion

    #region UniFiSshSettings Tests

    [Fact]
    public async Task GetUniFiSshSettingsAsync_ReturnsSettings()
    {
        _context.UniFiSshSettings.Add(new UniFiSshSettings
        {
            SiteId = 1,
            Username = "root",
            Port = 22,
            Enabled = true
        });
        await _context.SaveChangesAsync();

        var result = await _repository.GetUniFiSshSettingsAsync(1);

        result.Should().NotBeNull();
        result!.Username.Should().Be("root");
    }

    [Fact]
    public async Task SaveUniFiSshSettingsAsync_UpdatesExisting()
    {
        _context.UniFiSshSettings.Add(new UniFiSshSettings { SiteId = 1, Username = "old-user", Port = 22 });
        await _context.SaveChangesAsync();

        var updated = new UniFiSshSettings { SiteId = 1, Username = "new-user", Port = 2222 };

        await _repository.SaveUniFiSshSettingsAsync(1, updated);

        var count = await _context.UniFiSshSettings.CountAsync();
        count.Should().Be(1);
        var saved = await _context.UniFiSshSettings.FirstAsync();
        saved.Username.Should().Be("new-user");
        saved.Port.Should().Be(2222);
    }

    #endregion

    #region DeviceSshConfiguration Tests

    [Fact]
    public async Task GetDeviceSshConfigurationsAsync_ReturnsAllOrderedByName()
    {
        _context.DeviceSshConfigurations.AddRange(
            new DeviceSshConfiguration { SiteId = 1, Name = "Zebra", Host = "192.168.1.3" },
            new DeviceSshConfiguration { SiteId = 1, Name = "Alpha", Host = "192.168.1.1" },
            new DeviceSshConfiguration { SiteId = 1, Name = "Beta", Host = "192.168.1.2" }
        );
        await _context.SaveChangesAsync();

        var results = await _repository.GetDeviceSshConfigurationsAsync(1);

        results.Should().HaveCount(3);
        results[0].Name.Should().Be("Alpha");
        results[1].Name.Should().Be("Beta");
        results[2].Name.Should().Be("Zebra");
    }

    [Fact]
    public async Task GetDeviceSshConfigurationAsync_ReturnsById()
    {
        var device = new DeviceSshConfiguration { SiteId = 1, Name = "Test Device", Host = "192.168.1.1" };
        _context.DeviceSshConfigurations.Add(device);
        await _context.SaveChangesAsync();

        var result = await _repository.GetDeviceSshConfigurationAsync(1, device.Id);

        result.Should().NotBeNull();
        result!.Name.Should().Be("Test Device");
    }

    [Fact]
    public async Task SaveDeviceSshConfigurationAsync_CreatesNew()
    {
        var device = new DeviceSshConfiguration { SiteId = 1, Name = "New Device", Host = "192.168.1.100" };

        await _repository.SaveDeviceSshConfigurationAsync(1, device);

        var saved = await _context.DeviceSshConfigurations.FirstOrDefaultAsync(d => d.Name == "New Device");
        saved.Should().NotBeNull();
    }

    [Fact]
    public async Task SaveDeviceSshConfigurationAsync_UpdatesExisting()
    {
        var device = new DeviceSshConfiguration { SiteId = 1, Name = "Old Name", Host = "192.168.1.1" };
        _context.DeviceSshConfigurations.Add(device);
        await _context.SaveChangesAsync();

        device.Name = "Updated Name";
        device.Host = "192.168.1.2";

        await _repository.SaveDeviceSshConfigurationAsync(1, device);

        var saved = await _context.DeviceSshConfigurations.FindAsync(device.Id);
        saved!.Name.Should().Be("Updated Name");
        saved.Host.Should().Be("192.168.1.2");
    }

    [Fact]
    public async Task DeleteDeviceSshConfigurationAsync_RemovesDevice()
    {
        var device = new DeviceSshConfiguration { SiteId = 1, Name = "To Delete", Host = "192.168.1.1" };
        _context.DeviceSshConfigurations.Add(device);
        await _context.SaveChangesAsync();
        var id = device.Id;

        await _repository.DeleteDeviceSshConfigurationAsync(1, id);

        var deleted = await _context.DeviceSshConfigurations.FindAsync(id);
        deleted.Should().BeNull();
    }

    #endregion

    #region Multi-Site Isolation Tests

    [Fact]
    public async Task GetUniFiConnectionSettingsAsync_ReturnsOnlySettingsForRequestedSite()
    {
        // Arrange: Create settings for two different sites
        _context.UniFiConnectionSettings.AddRange(
            new UniFiConnectionSettings { SiteId = 1, ControllerUrl = "https://site1.unifi.local", Username = "admin1" },
            new UniFiConnectionSettings { SiteId = 2, ControllerUrl = "https://site2.unifi.local", Username = "admin2" }
        );
        await _context.SaveChangesAsync();

        // Act: Query for site 1
        var result = await _repository.GetUniFiConnectionSettingsAsync(1);

        // Assert: Should only return site 1's settings
        result.Should().NotBeNull();
        result!.ControllerUrl.Should().Be("https://site1.unifi.local");
        result.SiteId.Should().Be(1);
    }

    [Fact]
    public async Task GetDeviceSshConfigurationsAsync_ReturnsOnlyDevicesForRequestedSite()
    {
        // Arrange: Create devices for two different sites
        _context.DeviceSshConfigurations.AddRange(
            new DeviceSshConfiguration { SiteId = 1, Name = "Site1 Device", Host = "192.168.1.1" },
            new DeviceSshConfiguration { SiteId = 1, Name = "Site1 Device 2", Host = "192.168.1.2" },
            new DeviceSshConfiguration { SiteId = 2, Name = "Site2 Device", Host = "192.168.2.1" }
        );
        await _context.SaveChangesAsync();

        // Act: Query for site 1
        var result = await _repository.GetDeviceSshConfigurationsAsync(1);

        // Assert: Should only return site 1's devices
        result.Should().HaveCount(2);
        result.Should().OnlyContain(d => d.SiteId == 1);
        result.Should().NotContain(d => d.Name == "Site2 Device");
    }

    [Fact]
    public async Task GetDeviceSshConfigurationAsync_ReturnsNull_WhenDeviceBelongsToDifferentSite()
    {
        // Arrange: Create a device for site 2
        var device = new DeviceSshConfiguration { SiteId = 2, Name = "Site2 Device", Host = "192.168.2.1" };
        _context.DeviceSshConfigurations.Add(device);
        await _context.SaveChangesAsync();

        // Act: Try to get it using site 1
        var result = await _repository.GetDeviceSshConfigurationAsync(1, device.Id);

        // Assert: Should not be accessible from site 1
        result.Should().BeNull();
    }

    [Fact]
    public async Task DeleteDeviceSshConfigurationAsync_DoesNotDeleteDeviceFromDifferentSite()
    {
        // Arrange: Create a device for site 2
        var device = new DeviceSshConfiguration { SiteId = 2, Name = "Site2 Device", Host = "192.168.2.1" };
        _context.DeviceSshConfigurations.Add(device);
        await _context.SaveChangesAsync();
        var deviceId = device.Id;

        // Act: Try to delete it using site 1
        await _repository.DeleteDeviceSshConfigurationAsync(1, deviceId);

        // Assert: Device should still exist (wrong site)
        var stillExists = await _context.DeviceSshConfigurations.FindAsync(deviceId);
        stillExists.Should().NotBeNull();
    }

    #endregion
}
