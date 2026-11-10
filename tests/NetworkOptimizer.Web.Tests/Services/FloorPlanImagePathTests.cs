using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NetworkOptimizer.Storage.Models;
using NetworkOptimizer.Storage.Services;
using NetworkOptimizer.Web.Services;
using Xunit;

namespace NetworkOptimizer.Web.Tests.Services;

/// <summary>
/// Floor plan image paths: an app-written ImagePath resolves as it always has, and a stored path
/// that leaves the site's floor plan directory is neither served nor deleted.
/// </summary>
public class FloorPlanImagePathTests : IDisposable
{
    private static readonly byte[] PngHeader = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 0 };

    private readonly string _dir;
    private readonly string _floorPlanRoot;
    private readonly string _sentinel;
    private readonly SiteDbContextFactory _factory;
    private readonly SiteContextService _siteContext;

    public FloorPlanImagePathTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "no-floorplan-path-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_dir);
        _floorPlanRoot = Path.Combine(_dir, "floor-plans");
        _sentinel = Path.Combine(_dir, "sentinel.db");
        File.WriteAllText(_sentinel, "keep");

        var paths = new SiteDatabasePaths(Path.Combine(_dir, "network_optimizer.db"));
        _factory = new SiteDbContextFactory(paths);
        _siteContext = new SiteContextService(new HttpContextAccessor(), paths);
        using var db = _factory.CreateForSite(_siteContext.Slug, _siteContext.IsDefault);
        db.Database.Migrate();
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* temp dir; a leftover is harmless */ }
        GC.SuppressFinalize(this);
    }

    private FloorPlanService CreateService(string directory) =>
        new(_factory, _siteContext, NullLogger<FloorPlanService>.Instance, directory);

    private static string WriteFile(string directory, string relative)
    {
        var full = Path.Combine(directory, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllBytes(full, PngHeader);
        return full;
    }

    // --- Pinning: app-written paths ---

    public static TheoryData<string> AppWrittenPaths() => new()
    {
        "12/floor_1.png",
        Path.Combine("12", "floor_1_img_7.png"),
        Path.Combine("3", "floor_-1.png"),
    };

    [Theory]
    [MemberData(nameof(AppWrittenPaths))]
    public void GetFloorImagePath_AppWrittenPath_DefaultSite_ResolvesToCombinedPath(string relative)
    {
        var expected = WriteFile(_floorPlanRoot, relative);
        var service = CreateService(_floorPlanRoot);

        service.GetFloorImagePath(new FloorPlan { ImagePath = relative }).Should().Be(expected);
    }

    [Theory]
    [MemberData(nameof(AppWrittenPaths))]
    public void GetFloorImageFilePath_AppWrittenPath_NonDefaultSite_ResolvesToCombinedPath(string relative)
    {
        var siteDir = Path.Combine(_floorPlanRoot, "site-b");
        var expected = WriteFile(siteDir, relative);
        var service = CreateService(siteDir);

        service.GetFloorImageFilePath(new FloorPlanImage { ImagePath = relative }).Should().Be(expected);
    }

    [Fact]
    public void GetFloorImagePath_MissingFileOrEmptyPath_ReturnsNull()
    {
        var service = CreateService(_floorPlanRoot);

        service.GetFloorImagePath(new FloorPlan { ImagePath = "12/absent.png" }).Should().BeNull();
        service.GetFloorImagePath(new FloorPlan { ImagePath = null }).Should().BeNull();
        service.GetFloorImageFilePath(new FloorPlanImage { ImagePath = "" }).Should().BeNull();
    }

    [Fact]
    public async Task UploadReplaceDelete_EndToEnd_StillWorks()
    {
        var service = CreateService(_floorPlanRoot);
        var building = await service.CreateBuildingAsync("Main", 0, 0);
        var floor = await service.CreateFloorAsync(building.Id, 1, "Ground", 0, 0, 1, 1);

        var image = await service.CreateFloorImageAsync(floor.Id, new MemoryStream(PngHeader), 0, 0, 1, 1);
        var imageFile = service.GetFloorImageFilePath(image);
        imageFile.Should().NotBeNull();
        File.Exists(imageFile).Should().BeTrue();

        await service.SaveFloorImageAsync(floor.Id, new MemoryStream(PngHeader));
        await service.SaveFloorImageAsync(floor.Id, new MemoryStream(PngHeader));
        var legacyFile = service.GetFloorImagePath((await service.GetFloorAsync(floor.Id))!);
        legacyFile.Should().NotBeNull();

        (await service.DeleteFloorImageAsync(image.Id)).Should().BeTrue();
        File.Exists(imageFile).Should().BeFalse();

        (await service.DeleteFloorAsync(floor.Id)).Should().BeTrue();
        File.Exists(legacyFile).Should().BeFalse();
    }

    // --- Containment ---

    public static TheoryData<string> EscapingPaths() => new()
    {
        "../sentinel.db",
        "12/../../sentinel.db",
        Path.Combine("..", "sentinel.db"),
        "/etc/passwd",
        @"C:\Windows\win.ini",
        @"\\server\share\x.png",
    };

    [Theory]
    [MemberData(nameof(EscapingPaths))]
    public void GetFloorImagePath_EscapingPath_ReturnsNull(string relative)
    {
        var service = CreateService(_floorPlanRoot);

        service.GetFloorImagePath(new FloorPlan { ImagePath = relative }).Should().BeNull();
        service.GetFloorImageFilePath(new FloorPlanImage { ImagePath = relative }).Should().BeNull();
    }

    [Fact]
    public void GetFloorImagePath_RootedPathToExistingFile_ReturnsNull()
    {
        var service = CreateService(_floorPlanRoot);

        service.GetFloorImagePath(new FloorPlan { ImagePath = _sentinel }).Should().BeNull();
        service.GetFloorImageFilePath(new FloorPlanImage { ImagePath = _sentinel }).Should().BeNull();
    }

    [Fact]
    public void GetFloorImageFilePath_DefaultSiteReachingIntoOtherSite_ReturnsNull()
    {
        var otherSiteFile = Path.Combine("other-site", "1", "floor_1.png");
        WriteFile(_floorPlanRoot, otherSiteFile);
        var service = CreateService(_floorPlanRoot);

        service.GetFloorImageFilePath(new FloorPlanImage { ImagePath = otherSiteFile }).Should().BeNull();
        service.GetFloorImagePath(new FloorPlan { ImagePath = otherSiteFile }).Should().BeNull();
    }

    [Fact]
    public async Task DeleteFloor_StoredPathsOutsideDirectory_LeaveSentinelInPlace()
    {
        var service = CreateService(_floorPlanRoot);
        var building = await service.CreateBuildingAsync("Main", 0, 0);
        var floor = await service.CreateFloorAsync(building.Id, 1, "Ground", 0, 0, 1, 1);
        var image = await service.CreateFloorImageAsync(floor.Id, new MemoryStream(PngHeader), 0, 0, 1, 1);

        using (var db = _factory.CreateForSite(_siteContext.Slug, _siteContext.IsDefault))
        {
            (await db.FloorPlans.FindAsync(floor.Id))!.ImagePath = _sentinel;
            (await db.FloorPlanImages.FindAsync(image.Id))!.ImagePath = "12/../../sentinel.db";
            await db.SaveChangesAsync();
        }

        (await service.DeleteFloorAsync(floor.Id)).Should().BeTrue();

        File.Exists(_sentinel).Should().BeTrue();
    }

    [Fact]
    public async Task DeleteFloorImage_RootedStoredPath_LeavesSentinelInPlace()
    {
        var service = CreateService(_floorPlanRoot);
        var building = await service.CreateBuildingAsync("Main", 0, 0);
        var floor = await service.CreateFloorAsync(building.Id, 1, "Ground", 0, 0, 1, 1);
        var image = await service.CreateFloorImageAsync(floor.Id, new MemoryStream(PngHeader), 0, 0, 1, 1);

        using (var db = _factory.CreateForSite(_siteContext.Slug, _siteContext.IsDefault))
        {
            (await db.FloorPlanImages.FindAsync(image.Id))!.ImagePath = _sentinel;
            await db.SaveChangesAsync();
        }

        (await service.DeleteFloorImageAsync(image.Id)).Should().BeTrue();

        File.Exists(_sentinel).Should().BeTrue();
    }
}
