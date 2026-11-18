using System.Security.Claims;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NetworkOptimizer.Storage.Models;
using NetworkOptimizer.Storage.Models.Identity;
using NetworkOptimizer.Web.Services.Identity;
using NetworkOptimizer.Web.Services.Tours;
using Xunit;

namespace NetworkOptimizer.Web.Tests.Tours;

/// <summary>
/// Tour state is per user. An install with authentication disabled keeps its one default row, and
/// the only account on an install claims that row instead of being re-offered everything.
/// </summary>
public sealed class TourStateServiceTests : IDisposable
{
    private readonly string _mainDbPath = Path.Combine(Path.GetTempPath(), $"netopt-tours-main-{Guid.NewGuid():N}.db");
    private readonly string _authDbPath = Path.Combine(Path.GetTempPath(), $"netopt-tours-auth-{Guid.NewGuid():N}.db");
    private readonly TourStateService _state;

    private const string Alice = "user-1";
    private const string Bob = "user-2";
    private const string Default = TourState.DefaultSubject;

    public TourStateServiceTests()
    {
        var main = new MainFactory(_mainDbPath);
        var auth = new AuthFactory(_authDbPath);
        using (var db = main.CreateDbContext())
        {
            db.Database.EnsureCreated();
            db.AdminSettings.Add(new AdminSettings { FirstSeenVersion = "2.4.0", LastSeenAppVersion = "2.10.0" });
            db.SaveChanges();
        }
        using (var db = auth.CreateDbContext())
            db.Database.EnsureCreated();
        _state = new TourStateService(main, auth, NullLogger<TourStateService>.Instance);
    }

    // ---- subject resolution ----

    [Fact]
    public void SignedInUserIsTheirOwnSubject() =>
        TourStateService.SubjectFor(CallerInfo.ForUser(Principal(Alice), null, null, null)).Should().Be(Alice);

    [Fact]
    public void AuthenticationDisabledIsTheDefaultSubject() =>
        TourStateService.SubjectFor(CallerInfo.LocalNoAuth(null, null, null)).Should().Be(Default);

    [Fact]
    public void AnonymousAndSystemHaveNoSubject()
    {
        TourStateService.SubjectFor(CallerInfo.Anonymous(new ClaimsPrincipal(), null, null, null)).Should()
            .BeNull("the login page must never read or write the default row");
        TourStateService.SubjectFor(CallerInfo.System("test")).Should().BeNull();
        TourStateService.SubjectFor(null).Should().BeNull();
    }

    // ---- per-user state ----

    [Fact]
    public async Task TwoUsersHoldIndependentState()
    {
        await SeedUsersAsync(Alice, Bob);

        await _state.RecordStepSeenAsync(Alice, "step-a");
        await _state.RecordToursDismissedAsync(Alice, ["tour-x"]);
        await _state.RecordOfferAsync(Alice, ["tour-y"], "2.11.0");
        await _state.SetToursDisabledAsync(Alice, true);

        var bob = await _state.GetSnapshotAsync(Bob);
        bob.SeenStepIds.Should().BeEmpty();
        bob.DismissedTourIds.Should().BeEmpty();
        bob.Offers.Should().BeEmpty("one user's Later must not defer the tour for another");
        bob.ToursDisabled.Should().BeFalse();

        var alice = await _state.GetSnapshotAsync(Alice);
        alice.SeenStepIds.Should().BeEquivalentTo(["step-a"]);
        alice.DismissedTourIds.Should().BeEquivalentTo(["tour-x"]);
        alice.Offers.Should().ContainKey("tour-y");
        alice.ToursDisabled.Should().BeTrue();
    }

    [Fact]
    public async Task AUserAddedLaterIsStillOffered()
    {
        await SeedUsersAsync(Alice);
        await _state.RecordOfferAsync(Alice, ["tour-y"], "2.11.0");
        await SeedUsersAsync(Bob);

        (await _state.GetSnapshotAsync(Bob)).Offers.Should().BeEmpty();
    }

    [Fact]
    public async Task ANewUsersFirstWriteInsertsUnderTheirOwnSubject()
    {
        await SeedUsersAsync(Alice, Bob);

        await _state.RecordStepSeenAsync(Bob, "step-a");

        (await SubjectsAsync()).Should().BeEquivalentTo([Bob], "the model default subject must never leak into an insert");
    }

    // ---- authentication disabled ----

    [Fact]
    public async Task AuthDisabledKeepsTheDefaultRowEvenWithOneAccountRow()
    {
        await SeedUsersAsync(Alice);
        await _state.RecordStepSeenAsync(Default, "step-a");

        (await _state.GetSnapshotAsync(Default)).SeenStepIds.Should().BeEquivalentTo(["step-a"]);
        await _state.RecordStepSeenAsync(Default, "step-b");

        (await SubjectsAsync()).Should().BeEquivalentTo([Default],
            "an auth-disabled install that holds one account row keeps its state");
    }

    [Fact]
    public async Task NoSubjectNeitherReadsNorWritesTheDefaultRow()
    {
        await _state.RecordStepSeenAsync(Default, "step-a");

        (await _state.GetSnapshotAsync(null)).SeenStepIds.Should().BeEmpty();
        await _state.RecordStepSeenAsync(null, "step-b");
        await _state.SetToursDisabledAsync(null, true);
        await _state.ResetAsync(null);

        var row = await _state.GetSnapshotAsync(Default);
        row.SeenStepIds.Should().BeEquivalentTo(["step-a"]);
        row.ToursDisabled.Should().BeFalse();
        (await LastSeenAsync()).Should().Be("2.10.0");
    }

    // ---- claiming the legacy row ----

    [Fact]
    public async Task TheOnlyAccountClaimsTheDefaultRowOnFirstRead()
    {
        await _state.RecordToursDismissedAsync(Default, ["tour-x"]);
        await SeedUsersAsync(Alice);

        (await _state.GetSnapshotAsync(Alice)).DismissedTourIds.Should().BeEquivalentTo(["tour-x"],
            "the first offer decision must already see the history");
        (await SubjectsAsync()).Should().BeEquivalentTo([Alice]);
    }

    [Fact]
    public async Task TheOnlyAccountClaimsTheDefaultRowOnFirstWrite()
    {
        await _state.RecordToursDismissedAsync(Default, ["tour-x"]);
        await SeedUsersAsync(Alice);

        await _state.RecordStepSeenAsync(Alice, "step-a");

        (await SubjectsAsync()).Should().BeEquivalentTo([Alice]);
        (await _state.GetSnapshotAsync(Alice)).DismissedTourIds.Should().BeEquivalentTo(["tour-x"]);
    }

    [Fact]
    public async Task NoClaimWithTwoAccounts()
    {
        await _state.RecordToursDismissedAsync(Default, ["tour-x"]);
        await SeedUsersAsync(Alice, Bob);

        (await _state.GetSnapshotAsync(Alice)).DismissedTourIds.Should().BeEmpty();
        await _state.RecordStepSeenAsync(Alice, "step-a");

        (await SubjectsAsync()).Should().BeEquivalentTo([Default, Alice], "the legacy row is left orphaned");
    }

    [Fact]
    public async Task NoClaimByASubjectThatIsNotTheOnlyAccount()
    {
        await _state.RecordToursDismissedAsync(Default, ["tour-x"]);
        await SeedUsersAsync(Alice);

        (await _state.GetSnapshotAsync(Bob)).DismissedTourIds.Should().BeEmpty();
        (await SubjectsAsync()).Should().BeEquivalentTo([Default]);
    }

    // ---- reset ----

    [Fact]
    public async Task ResetRemovesOnlyTheCallersRowAndStillClearsLastSeen()
    {
        await SeedUsersAsync(Alice, Bob);
        await _state.RecordStepSeenAsync(Alice, "step-a");
        await _state.RecordStepSeenAsync(Bob, "step-b");

        await _state.ResetAsync(Alice);

        (await SubjectsAsync()).Should().BeEquivalentTo([Bob]);
        (await LastSeenAsync()).Should().BeNull("reset must keep re-arming the automatic offer");
        (await _state.GetFirstSeenVersionAsync()).Should().Be("2.4.0", "FirstSeenVersion cannot be reconstructed");
    }

    [Fact]
    public async Task ResetByTheOnlyAccountAlsoRemovesAnUnclaimedDefaultRow()
    {
        await _state.RecordToursDismissedAsync(Default, ["tour-x"]);
        await SeedUsersAsync(Alice);

        await _state.ResetAsync(Alice);

        (await SubjectsAsync()).Should().BeEmpty("otherwise the next read would claim the history back");
    }

    // ---- helpers ----

    private async Task SeedUsersAsync(params string[] ids)
    {
        await using var db = new AuthFactory(_authDbPath).CreateDbContext();
        foreach (var id in ids)
            db.Users.Add(new ApplicationUser
            {
                Id = id,
                UserName = id,
                NormalizedUserName = id.ToUpperInvariant(),
                SecurityStamp = Guid.NewGuid().ToString(),
            });
        await db.SaveChangesAsync();
    }

    private async Task<List<string>> SubjectsAsync()
    {
        await using var db = new MainFactory(_mainDbPath).CreateDbContext();
        return await db.TourStates.Select(t => t.Subject).ToListAsync();
    }

    private async Task<string?> LastSeenAsync()
    {
        await using var db = new MainFactory(_mainDbPath).CreateDbContext();
        return (await db.AdminSettings.SingleAsync()).LastSeenAppVersion;
    }

    private static ClaimsPrincipal Principal(string userId) =>
        new(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, userId)], "test"));

    private sealed class MainFactory(string path) : IDbContextFactory<NetworkOptimizerDbContext>
    {
        public NetworkOptimizerDbContext CreateDbContext() => new(
            new DbContextOptionsBuilder<NetworkOptimizerDbContext>().UseSqlite($"Data Source={path};Pooling=False").Options);
    }

    private sealed class AuthFactory(string path) : IDbContextFactory<AuthDbContext>
    {
        public AuthDbContext CreateDbContext() => new(
            new DbContextOptionsBuilder<AuthDbContext>().UseSqlite($"Data Source={path};Pooling=False").Options);
    }

    public void Dispose()
    {
        foreach (var path in new[] { _mainDbPath, _authDbPath })
        {
            try
            {
                if (File.Exists(path)) File.Delete(path);
            }
            catch (IOException) { /* temp file, ignored */ }
        }
    }
}
