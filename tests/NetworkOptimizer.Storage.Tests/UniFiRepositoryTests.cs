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
    public async Task SaveDeviceSshConfigurationAsync_UpdateWithBlankPassword_KeepsStoredPassword()
    {
        // The edit form never round-trips the stored encrypted password; a blank
        // incoming password on update means "unchanged", not "clear".
        var device = new DeviceSshConfiguration
        {
            Name = "Device",
            Host = "192.168.1.1",
            SshUsername = "User1",
            SshPassword = "encrypted-blob"
        };
        _context.DeviceSshConfigurations.Add(device);
        await _context.SaveChangesAsync();

        var update = new DeviceSshConfiguration
        {
            Id = device.Id,
            Name = "Device",
            Host = "192.168.1.1",
            SshUsername = "User2",
            SshPassword = null
        };
        await _repository.SaveDeviceSshConfigurationAsync(update);

        var saved = await _context.DeviceSshConfigurations.FindAsync(device.Id);
        saved!.SshUsername.Should().Be("User2");
        saved.SshPassword.Should().Be("encrypted-blob");
    }

    [Fact]
    public async Task SaveDeviceSshConfigurationAsync_UpdateWithNewPassword_Overwrites()
    {
        var device = new DeviceSshConfiguration
        {
            Name = "Device",
            Host = "192.168.1.1",
            SshPassword = "old-blob"
        };
        _context.DeviceSshConfigurations.Add(device);
        await _context.SaveChangesAsync();

        var update = new DeviceSshConfiguration
        {
            Id = device.Id,
            Name = "Device",
            Host = "192.168.1.1",
            SshPassword = "new-blob"
        };
        await _repository.SaveDeviceSshConfigurationAsync(update);

        var saved = await _context.DeviceSshConfigurations.FindAsync(device.Id);
        saved!.SshPassword.Should().Be("new-blob");
    }

    [Fact]
    public async Task SaveDeviceSshConfigurationAsync_UpdateWithKeyPath_ClearsStoredPassword()
    {
        // Switching to key auth drops the stored password (matching the gateway and
        // UniFi SSH settings pages), so a broken key fails loudly instead of silently
        // falling back to the old password.
        var device = new DeviceSshConfiguration
        {
            Name = "Device",
            Host = "192.168.1.1",
            SshPassword = "encrypted-blob"
        };
        _context.DeviceSshConfigurations.Add(device);
        await _context.SaveChangesAsync();

        var update = new DeviceSshConfiguration
        {
            Id = device.Id,
            Name = "Device",
            Host = "192.168.1.1",
            SshPassword = null,
            SshPrivateKeyPath = "/app/ssh-keys/id_test"
        };
        await _repository.SaveDeviceSshConfigurationAsync(update);

        var saved = await _context.DeviceSshConfigurations.FindAsync(device.Id);
        saved!.SshPassword.Should().BeNull();
        saved.SshPrivateKeyPath.Should().Be("/app/ssh-keys/id_test");
    }

    [Fact]
    public async Task SaveDeviceSshConfigurationAsync_KeyPathWinsOverTypedPassword()
    {
        var device = new DeviceSshConfiguration
        {
            Name = "Device",
            Host = "192.168.1.1",
            SshPassword = "typed-blob",
            SshPrivateKeyPath = "/app/ssh-keys/id_test"
        };

        await _repository.SaveDeviceSshConfigurationAsync(device);

        var saved = await _context.DeviceSshConfigurations.FirstOrDefaultAsync(d => d.Name == "Device");
        saved!.SshPassword.Should().BeNull();
        saved.SshPrivateKeyPath.Should().Be("/app/ssh-keys/id_test");
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
