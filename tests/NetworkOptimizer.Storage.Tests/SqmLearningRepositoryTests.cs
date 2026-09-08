using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;
using NetworkOptimizer.Storage.Models;
using NetworkOptimizer.Storage.Repositories;
using Xunit;

namespace NetworkOptimizer.Storage.Tests;

public class SqmLearningRepositoryTests : IDisposable
{
    private readonly NetworkOptimizerDbContext _context;
    private readonly SqmLearningRepository _repository;

    public SqmLearningRepositoryTests()
    {
        var options = new DbContextOptionsBuilder<NetworkOptimizerDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;
        _context = new NetworkOptimizerDbContext(options);
        _repository = new SqmLearningRepository(_context, new Mock<ILogger<SqmLearningRepository>>().Object);
    }

    public void Dispose() => _context.Dispose();

    [Fact]
    public async Task SaveProfile_InsertsThenUpdatesByWanNumber()
    {
        await _repository.SaveProfileAsync(new SqmCongestionProfile { WanNumber = 1, Interface = "eth4", Name = "Cell" });
        await _repository.SaveProfileAsync(new SqmCongestionProfile { WanNumber = 1, Interface = "eth4", Name = "Cell", ValidSampleCount = 12, IsReliable = true, LastError = "x" });

        var all = await _repository.GetAllProfilesAsync();
        all.Should().ContainSingle();
        all[0].ValidSampleCount.Should().Be(12);
        all[0].IsReliable.Should().BeTrue();
        all[0].LastError.Should().Be("x");
    }

    [Fact]
    public async Task Samples_RoundTrip_FilterBySince_AndOrderAscending()
    {
        var t0 = new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc);
        await _repository.AddSampleAsync(new SqmLearningSample { WanNumber = 1, SampledAt = t0.AddHours(2), DownloadMbps = 100, UploadMbps = 10, Success = true });
        await _repository.AddSampleAsync(new SqmLearningSample { WanNumber = 1, SampledAt = t0, DownloadMbps = 90, UploadMbps = 9, Success = true });
        await _repository.AddSampleAsync(new SqmLearningSample { WanNumber = 2, SampledAt = t0, DownloadMbps = 5, UploadMbps = 1, Success = true });

        var all = await _repository.GetSamplesAsync(1);
        all.Select(s => s.DownloadMbps).Should().Equal(90, 100);

        var since = await _repository.GetSamplesAsync(1, t0.AddHours(1));
        since.Should().ContainSingle(s => s.DownloadMbps == 100);
    }

    [Fact]
    public async Task SetSampleExclusions_MarksListed_AndClearsTheRest()
    {
        var a = await _repository.AddSampleAsync(new SqmLearningSample { WanNumber = 1, Success = true, Excluded = true, ExclusionReason = "old" });
        var b = await _repository.AddSampleAsync(new SqmLearningSample { WanNumber = 1, Success = true });

        await _repository.SetSampleExclusionsAsync(1, new Dictionary<int, string> { [b] = "download 20 Mbps against 200 Mbps typical at 03:00" });

        var samples = await _repository.GetSamplesAsync(1);
        samples.Single(s => s.Id == a).Excluded.Should().BeFalse();
        samples.Single(s => s.Id == a).ExclusionReason.Should().BeNull();
        samples.Single(s => s.Id == b).Excluded.Should().BeTrue();
        samples.Single(s => s.Id == b).ExclusionReason.Should().Contain("03:00");
    }

    [Fact]
    public async Task DeleteProfile_RemovesRowAndSamples_ForThatWanOnly()
    {
        await _repository.SaveProfileAsync(new SqmCongestionProfile { WanNumber = 1 });
        await _repository.SaveProfileAsync(new SqmCongestionProfile { WanNumber = 2 });
        await _repository.AddSampleAsync(new SqmLearningSample { WanNumber = 1, Success = true });
        await _repository.AddSampleAsync(new SqmLearningSample { WanNumber = 2, Success = true });

        await _repository.DeleteProfileAsync(1);

        (await _repository.GetProfileAsync(1)).Should().BeNull();
        (await _repository.GetProfileAsync(2)).Should().NotBeNull();
        (await _repository.GetSamplesAsync(1)).Should().BeEmpty();
        (await _repository.GetSamplesAsync(2)).Should().ContainSingle();
    }

    [Fact]
    public void HasProfile_RequiresCurveAndSamples()
    {
        new SqmCongestionProfile().HasProfile.Should().BeFalse();
        new SqmCongestionProfile { DownloadMultipliersJson = "[1]" }.HasProfile.Should().BeFalse();
        new SqmCongestionProfile { DownloadMultipliersJson = "[1]", ValidSampleCount = 1 }.HasProfile.Should().BeTrue();
    }
}
