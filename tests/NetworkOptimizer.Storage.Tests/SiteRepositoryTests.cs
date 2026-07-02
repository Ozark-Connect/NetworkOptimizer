using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;
using NetworkOptimizer.Storage.Models;
using NetworkOptimizer.Storage.Repositories;
using Xunit;

namespace NetworkOptimizer.Storage.Tests;

public class SiteRepositoryTests
{
    private sealed class TestDbFactory : IDbContextFactory<NetworkOptimizerDbContext>
    {
        private readonly DbContextOptions<NetworkOptimizerDbContext> _options;
        public TestDbFactory(DbContextOptions<NetworkOptimizerDbContext> options) => _options = options;
        public NetworkOptimizerDbContext CreateDbContext() => new(_options);
    }

    private readonly SiteRepository _repository;
    private readonly TestDbFactory _factory;

    public SiteRepositoryTests()
    {
        var options = new DbContextOptionsBuilder<NetworkOptimizerDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;
        _factory = new TestDbFactory(options);
        _repository = new SiteRepository(_factory, new Mock<ILogger<SiteRepository>>().Object);
    }

    [Fact]
    public async Task AddAsync_PersistsSiteWithTimestamps()
    {
        var site = await _repository.AddAsync(new Site { Slug = "lake-house", Name = "Lake House" });

        site.Id.Should().BeGreaterThan(0);
        site.CreatedAt.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromSeconds(5));

        var fetched = await _repository.GetBySlugAsync("lake-house");
        fetched.Should().NotBeNull();
        fetched!.Name.Should().Be("Lake House");
    }

    [Fact]
    public async Task GetAllAsync_OrdersBySortOrderThenName()
    {
        await _repository.AddAsync(new Site { Slug = "b-site", Name = "Beta", SortOrder = 1 });
        await _repository.AddAsync(new Site { Slug = "a-site", Name = "Alpha", SortOrder = 2 });
        await _repository.AddAsync(new Site { Slug = "main", Name = "Main Site", SortOrder = 0, IsDefault = true });

        var all = await _repository.GetAllAsync();

        all.Select(s => s.Slug).Should().ContainInOrder("main", "b-site", "a-site");
    }

    [Fact]
    public async Task GetBySlugAsync_ReturnsNullForUnknown()
    {
        (await _repository.GetBySlugAsync("nope")).Should().BeNull();
    }

    [Fact]
    public async Task GetDefaultAsync_ReturnsDefaultSite()
    {
        await _repository.AddAsync(new Site { Slug = "other", Name = "Other" });
        await _repository.AddAsync(new Site { Slug = "main", Name = "Main Site", IsDefault = true });

        var def = await _repository.GetDefaultAsync();

        def.Should().NotBeNull();
        def!.Slug.Should().Be("main");
    }

    [Fact]
    public async Task UpdateAsync_UpdatesMutableFieldsButNeverSlug()
    {
        var site = await _repository.AddAsync(new Site { Slug = "office", Name = "Office" });

        await _repository.UpdateAsync(new Site
        {
            Id = site.Id,
            Slug = "renamed-slug-attempt",
            Name = "Office HQ",
            Enabled = false,
            SortOrder = 5,
            Notes = "back building"
        });

        var updated = await _repository.GetBySlugAsync("office");
        updated.Should().NotBeNull();
        updated!.Name.Should().Be("Office HQ");
        updated.Enabled.Should().BeFalse();
        updated.SortOrder.Should().Be(5);
        updated.Notes.Should().Be("back building");
        updated.Slug.Should().Be("office");
    }

    [Fact]
    public async Task UpdateAsync_ThrowsForUnknownId()
    {
        var act = () => _repository.UpdateAsync(new Site { Id = 999, Slug = "x", Name = "X" });
        await act.Should().ThrowAsync<InvalidOperationException>();
    }
}
