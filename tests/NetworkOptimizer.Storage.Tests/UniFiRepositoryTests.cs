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
            ControllerUrl = "https://unifi.local",
            Username = "admin",
            Site = "default"
        });
        await _context.SaveChangesAsync();

        var result = await _repository.GetUniFiConnectionSettingsAsync();

        result.Should().NotBeNull();
        result!.ControllerUrl.Should().Be("https://unifi.local");
    }

    [Fact]
    public async Task GetUniFiConnectionSettingsAsync_ReturnsNullWhenEmpty()
    {
        var result = await _repository.GetUniFiConnectionSettingsAsync();
        result.Should().BeNull();
    }

    [Fact]
    public async Task SaveUniFiConnectionSettingsAsync_CreatesSettings()
    {
        var settings = new UniFiConnectionSettings
        {
            ControllerUrl = "https://new-unifi.local",
            Username = "admin",
            Site = "default"
        };

        await _repository.SaveUniFiConnectionSettingsAsync(settings);

        var saved = await _context.UniFiConnectionSettings.FirstOrDefaultAsync();
        saved.Should().NotBeNull();
        saved!.ControllerUrl.Should().Be("https://new-unifi.local");
    }

    [Fact]
    public async Task SaveUniFiConnectionSettingsAsync_UpdatesExisting()
    {
        _context.UniFiConnectionSettings.Add(new UniFiConnectionSettings
        {
            ControllerUrl = "https://old.local",
            Username = "old-admin"
        });
        await _context.SaveChangesAsync();

        var updated = new UniFiConnectionSettings
        {
            ControllerUrl = "https://new.local",
            Username = "new-admin"
        };

        await _repository.SaveUniFiConnectionSettingsAsync(updated);

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
            Username = "root",
            Port = 22,
            Enabled = true
        });
        await _context.SaveChangesAsync();

        var result = await _repository.GetUniFiSshSettingsAsync();

        result.Should().NotBeNull();
        result!.Username.Should().Be("root");
    }

    [Fact]
    public async Task SaveUniFiSshSettingsAsync_UpdatesExisting()
    {
        _context.UniFiSshSettings.Add(new UniFiSshSettings { Username = "old-user", Port = 22 });
        await _context.SaveChangesAsync();

        var updated = new UniFiSshSettings { Username = "new-user", Port = 2222 };

        await _repository.SaveUniFiSshSettingsAsync(updated);

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
            new DeviceSshConfiguration { Name = "Zebra", Host = "192.168.1.3" },
            new DeviceSshConfiguration { Name = "Alpha", Host = "192.168.1.1" },
            new DeviceSshConfiguration { Name = "Beta", Host = "192.168.1.2" }
        );
        await _context.SaveChangesAsync();

        var results = await _repository.GetDeviceSshConfigurationsAsync();

        results.Should().HaveCount(3);
        results[0].Name.Should().Be("Alpha");
        results[1].Name.Should().Be("Beta");
        results[2].Name.Should().Be("Zebra");
    }

    [Fact]
    public async Task GetDeviceSshConfigurationAsync_ReturnsById()
    {
        var device = new DeviceSshConfiguration { Name = "Test Device", Host = "192.168.1.1" };
        _context.DeviceSshConfigurations.Add(device);
        await _context.SaveChangesAsync();

        var result = await _repository.GetDeviceSshConfigurationAsync(device.Id);

        result.Should().NotBeNull();
        result!.Name.Should().Be("Test Device");
    }

    [Fact]
    public async Task SaveDeviceSshConfigurationAsync_CreatesNew()
    {
        var device = new DeviceSshConfiguration { Name = "New Device", Host = "192.168.1.100" };

        await _repository.SaveDeviceSshConfigurationAsync(device);

        var saved = await _context.DeviceSshConfigurations.FirstOrDefaultAsync(d => d.Name == "New Device");
        saved.Should().NotBeNull();
    }

    [Fact]
    public async Task SaveDeviceSshConfigurationAsync_UpdatesExisting()
    {
        var device = new DeviceSshConfiguration { Name = "Old Name", Host = "192.168.1.1" };
        _context.DeviceSshConfigurations.Add(device);
        await _context.SaveChangesAsync();

        device.Name = "Updated Name";
        device.Host = "192.168.1.2";

        await _repository.SaveDeviceSshConfigurationAsync(device);

        var saved = await _context.DeviceSshConfigurations.FindAsync(device.Id);
        saved!.Name.Should().Be("Updated Name");
        saved.Host.Should().Be("192.168.1.2");
    }

    [Fact]
    public async Task DeleteDeviceSshConfigurationAsync_RemovesDevice()
    {
        var device = new DeviceSshConfiguration { Name = "To Delete", Host = "192.168.1.1" };
        _context.DeviceSshConfigurations.Add(device);
        await _context.SaveChangesAsync();
        var id = device.Id;

        await _repository.DeleteDeviceSshConfigurationAsync(id);

        var deleted = await _context.DeviceSshConfigurations.FindAsync(id);
        deleted.Should().BeNull();
    }

    #endregion
}
