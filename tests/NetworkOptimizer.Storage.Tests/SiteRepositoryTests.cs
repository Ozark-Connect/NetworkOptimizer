using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;
using NetworkOptimizer.Storage.Models;
using NetworkOptimizer.Storage.Repositories;
using Xunit;

namespace NetworkOptimizer.Storage.Tests;

public class SiteRepositoryTests : IDisposable
{
    private readonly NetworkOptimizerDbContext _context;
    private readonly SiteRepository _repository;

    public SiteRepositoryTests()
    {
        var options = new DbContextOptionsBuilder<NetworkOptimizerDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;

        _context = new NetworkOptimizerDbContext(options);
        var logger = new Mock<ILogger<SiteRepository>>();
        _repository = new SiteRepository(_context, logger.Object);
    }

    public void Dispose()
    {
        _context.Dispose();
    }

    #region GetSiteAsync Tests

    [Fact]
    public async Task GetSiteAsync_ReturnsSite_WhenExists()
    {
        var site = new Site { Name = "Test Site", Enabled = true };
        _context.Sites.Add(site);
        await _context.SaveChangesAsync();

        var result = await _repository.GetSiteAsync(site.Id);

        result.Should().NotBeNull();
        result!.Name.Should().Be("Test Site");
    }

    [Fact]
    public async Task GetSiteAsync_ReturnsNull_WhenNotFound()
    {
        var result = await _repository.GetSiteAsync(999);

        result.Should().BeNull();
    }

    #endregion

    #region GetAllSitesAsync Tests

    [Fact]
    public async Task GetAllSitesAsync_ReturnsOnlyEnabledSites_ByDefault()
    {
        _context.Sites.AddRange(
            new Site { Name = "Enabled Site", Enabled = true },
            new Site { Name = "Disabled Site", Enabled = false }
        );
        await _context.SaveChangesAsync();

        var result = await _repository.GetAllSitesAsync();

        result.Should().HaveCount(1);
        result[0].Name.Should().Be("Enabled Site");
    }

    [Fact]
    public async Task GetAllSitesAsync_ReturnsAllSites_WhenIncludeDisabled()
    {
        _context.Sites.AddRange(
            new Site { Name = "Enabled Site", Enabled = true },
            new Site { Name = "Disabled Site", Enabled = false }
        );
        await _context.SaveChangesAsync();

        var result = await _repository.GetAllSitesAsync(includeDisabled: true);

        result.Should().HaveCount(2);
    }

    [Fact]
    public async Task GetAllSitesAsync_ReturnsSortedBySortOrderThenName()
    {
        _context.Sites.AddRange(
            new Site { Name = "Zebra", SortOrder = 1, Enabled = true },
            new Site { Name = "Alpha", SortOrder = 2, Enabled = true },
            new Site { Name = "Beta", SortOrder = 1, Enabled = true }
        );
        await _context.SaveChangesAsync();

        var result = await _repository.GetAllSitesAsync();

        result.Should().HaveCount(3);
        result[0].Name.Should().Be("Beta");   // SortOrder 1, alphabetically first
        result[1].Name.Should().Be("Zebra");  // SortOrder 1, alphabetically second
        result[2].Name.Should().Be("Alpha");  // SortOrder 2
    }

    #endregion

    #region GetSiteCountAsync Tests

    [Fact]
    public async Task GetSiteCountAsync_ReturnsOnlyEnabledCount()
    {
        _context.Sites.AddRange(
            new Site { Name = "Site 1", Enabled = true },
            new Site { Name = "Site 2", Enabled = true },
            new Site { Name = "Site 3", Enabled = false }
        );
        await _context.SaveChangesAsync();

        var count = await _repository.GetSiteCountAsync();

        count.Should().Be(2);
    }

    [Fact]
    public async Task GetSiteCountAsync_ReturnsZero_WhenNoSites()
    {
        var count = await _repository.GetSiteCountAsync();

        count.Should().Be(0);
    }

    #endregion

    #region CreateSiteAsync Tests

    [Fact]
    public async Task CreateSiteAsync_CreatesSiteAndReturnsId()
    {
        var site = new Site { Name = "New Site" };

        var id = await _repository.CreateSiteAsync(site);

        id.Should().BeGreaterThan(0);
        var saved = await _context.Sites.FindAsync(id);
        saved.Should().NotBeNull();
        saved!.Name.Should().Be("New Site");
    }

    [Fact]
    public async Task CreateSiteAsync_SetsTimestamps()
    {
        var site = new Site { Name = "Timestamped Site" };
        var beforeCreate = DateTime.UtcNow;

        await _repository.CreateSiteAsync(site);

        var saved = await _context.Sites.FirstAsync();
        saved.CreatedAt.Should().BeOnOrAfter(beforeCreate);
        saved.UpdatedAt.Should().BeOnOrAfter(beforeCreate);
    }

    #endregion

    #region UpdateSiteAsync Tests

    [Fact]
    public async Task UpdateSiteAsync_UpdatesSiteProperties()
    {
        var site = new Site { Name = "Original Name", DisplayName = "Original Display", Notes = "Original Notes" };
        _context.Sites.Add(site);
        await _context.SaveChangesAsync();

        site.Name = "Updated Name";
        site.DisplayName = "Updated Display";
        site.Notes = "Updated Notes";
        site.SortOrder = 5;

        await _repository.UpdateSiteAsync(site);

        var updated = await _context.Sites.FindAsync(site.Id);
        updated!.Name.Should().Be("Updated Name");
        updated.DisplayName.Should().Be("Updated Display");
        updated.Notes.Should().Be("Updated Notes");
        updated.SortOrder.Should().Be(5);
    }

    [Fact]
    public async Task UpdateSiteAsync_UpdatesTimestamp()
    {
        var site = new Site { Name = "Site", UpdatedAt = DateTime.UtcNow.AddDays(-1) };
        _context.Sites.Add(site);
        await _context.SaveChangesAsync();
        var originalUpdatedAt = site.UpdatedAt;

        site.Name = "Updated";
        await _repository.UpdateSiteAsync(site);

        var updated = await _context.Sites.FindAsync(site.Id);
        updated!.UpdatedAt.Should().BeAfter(originalUpdatedAt);
    }

    [Fact]
    public async Task UpdateSiteAsync_ThrowsWhenNotFound()
    {
        var site = new Site { Id = 999, Name = "Non-existent" };

        var act = async () => await _repository.UpdateSiteAsync(site);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*999*not found*");
    }

    #endregion

    #region DeleteSiteAsync Tests

    [Fact]
    public async Task DeleteSiteAsync_RemovesSite()
    {
        var site = new Site { Name = "To Delete" };
        _context.Sites.Add(site);
        await _context.SaveChangesAsync();
        var id = site.Id;

        await _repository.DeleteSiteAsync(id);

        var deleted = await _context.Sites.FindAsync(id);
        deleted.Should().BeNull();
    }

    [Fact]
    public async Task DeleteSiteAsync_DoesNothingWhenNotFound()
    {
        // Should not throw
        await _repository.DeleteSiteAsync(999);
    }

    #endregion

    #region GetSiteWithConnectionSettingsAsync Tests

    [Fact]
    public async Task GetSiteWithConnectionSettingsAsync_IncludesConnectionSettings()
    {
        var site = new Site { Name = "Site With Settings" };
        _context.Sites.Add(site);
        await _context.SaveChangesAsync();

        _context.UniFiConnectionSettings.Add(new UniFiConnectionSettings
        {
            SiteId = site.Id,
            ControllerUrl = "https://unifi.local",
            Username = "admin"
        });
        await _context.SaveChangesAsync();

        var result = await _repository.GetSiteWithConnectionSettingsAsync(site.Id);

        result.Should().NotBeNull();
        result!.ConnectionSettings.Should().NotBeNull();
        result.ConnectionSettings!.ControllerUrl.Should().Be("https://unifi.local");
    }

    [Fact]
    public async Task GetSiteWithConnectionSettingsAsync_ReturnsNull_WhenSiteNotFound()
    {
        var result = await _repository.GetSiteWithConnectionSettingsAsync(999);

        result.Should().BeNull();
    }

    #endregion

    #region GetSiteWithAllSettingsAsync Tests

    [Fact]
    public async Task GetSiteWithAllSettingsAsync_IncludesAllSettings()
    {
        var site = new Site { Name = "Full Settings Site" };
        _context.Sites.Add(site);
        await _context.SaveChangesAsync();

        _context.UniFiConnectionSettings.Add(new UniFiConnectionSettings
        {
            SiteId = site.Id,
            ControllerUrl = "https://unifi.local",
            Username = "admin"
        });
        _context.UniFiSshSettings.Add(new UniFiSshSettings
        {
            SiteId = site.Id,
            Username = "root",
            Port = 22
        });
        _context.GatewaySshSettings.Add(new GatewaySshSettings
        {
            SiteId = site.Id,
            Host = "192.168.1.1",
            Username = "root"
        });
        await _context.SaveChangesAsync();

        var result = await _repository.GetSiteWithAllSettingsAsync(site.Id);

        result.Should().NotBeNull();
        result!.ConnectionSettings.Should().NotBeNull();
        result.UniFiSshSettings.Should().NotBeNull();
        result.GatewaySshSettings.Should().NotBeNull();
        result.ConnectionSettings!.ControllerUrl.Should().Be("https://unifi.local");
        result.UniFiSshSettings!.Username.Should().Be("root");
        result.GatewaySshSettings!.Host.Should().Be("192.168.1.1");
    }

    #endregion
}
