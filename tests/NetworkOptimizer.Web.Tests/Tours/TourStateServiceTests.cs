using System.Security.Claims;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NetworkOptimizer.Storage.Models;
using NetworkOptimizer.Storage.Models.Identity;
using NetworkOptimizer.Web.Services;
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
    private readonly TourDefinitionService _definitions;

    private const string Alice = "user-1";
    private const string Bob = "user-2";
    private const string Default = TourState.DefaultSubject;

    // The minor line the service judges against: the build's release, or the newest test tour on a
    // source build. Two tours in it and two from long before it, all newer than no FirstSeenVersion.
    private static readonly Version Current = TourVersions.IsSourceBuild
        ? new Version(2, 11, 4)
        : TourVersions.Parse(AppVersionInfo.ReleaseVersion)!;
    private static readonly string CurrentMinorFirst = $"{Current.Major}.{Current.Minor}.0";
    private static readonly string CurrentMinorLatest = $"{Current.Major}.{Current.Minor}.{Math.Max(Current.Build, 0)}";
    private const string OldTour = "1.8.0";
    private const string OlderPatchTour = "1.9.2";

    public TourStateServiceTests()
    {
        var main = new MainFactory(_mainDbPath);
        var auth = new AuthFactory(_authDbPath);
        _definitions = new TourDefinitionService(
            new[] { OldTour, OlderPatchTour, CurrentMinorFirst, CurrentMinorLatest }.Distinct().Select(Tour),
            NullLogger<TourDefinitionService>.Instance);
        using (var db = main.CreateDbContext())
        {
            db.Database.EnsureCreated();
            db.AdminSettings.Add(new AdminSettings { FirstSeenVersion = "2.4.0", LastSeenAppVersion = "2.10.0" });
            db.SaveChanges();
        }
        using (var db = auth.CreateDbContext())
            db.Database.EnsureCreated();
        _state = new TourStateService(main, auth, _definitions, NullLogger<TourStateService>.Instance);
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
        bob.DismissedTourIds.Should().BeEquivalentTo([OldTour, OlderPatchTour], "only the clean-start baseline");
        bob.Offers.Should().BeEmpty("one user's Later must not defer the tour for another");
        bob.ToursDisabled.Should().BeFalse();

        var alice = await _state.GetSnapshotAsync(Alice);
        alice.SeenStepIds.Should().BeEquivalentTo(["step-a"]);
        alice.DismissedTourIds.Should().BeEquivalentTo([OldTour, OlderPatchTour, "tour-x"]);
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

        (await _state.GetSnapshotAsync(Alice)).DismissedTourIds.Should().NotContain("tour-x");
        await _state.RecordStepSeenAsync(Alice, "step-a");

        (await SubjectsAsync()).Should().BeEquivalentTo([Default, Alice], "the legacy row is left orphaned");
    }

    [Fact]
    public async Task NoClaimByASubjectThatIsNotTheOnlyAccount()
    {
        await _state.RecordToursDismissedAsync(Default, ["tour-x"]);
        await SeedUsersAsync(Alice);

        (await _state.GetSnapshotAsync(Bob)).DismissedTourIds.Should().NotContain("tour-x");
        (await SubjectsAsync()).Should().BeEquivalentTo([Default]);
    }

    [Fact]
    public async Task TurningAuthenticationOffHandsTheOnlyAccountsRowBack()
    {
        await SeedUsersAsync(Alice);
        await _state.RecordToursDismissedAsync(Alice, ["tour-x"]);

        (await _state.GetSnapshotAsync(Default)).DismissedTourIds.Should().Contain("tour-x",
            "with one account, the auth-disabled install is the same person");
        (await SubjectsAsync()).Should().BeEquivalentTo([Default]);

        (await _state.GetSnapshotAsync(Alice)).DismissedTourIds.Should().Contain("tour-x",
            "turning authentication back on claims it again");
    }

    [Fact]
    public async Task NoHandBackWithTwoAccounts()
    {
        await SeedUsersAsync(Alice, Bob);
        await _state.RecordToursDismissedAsync(Alice, ["tour-x"]);

        (await _state.GetSnapshotAsync(Default)).DismissedTourIds.Should().BeEmpty();
        (await SubjectsAsync()).Should().BeEquivalentTo([Alice]);
    }

    // ---- the legacy opt-out ----

    [Fact]
    public async Task TheLegacyOptOutCarriesToAccountsThatExisted()
    {
        await SeedUsersAsync(Alice, Bob);
        await _state.SetToursDisabledAsync(Default, true);

        (await _state.GetSnapshotAsync(Bob)).ToursDisabled.Should().BeTrue("the opt-out covered every account");
        await _state.RecordStepSeenAsync(Bob, "step-a");
        (await _state.GetSnapshotAsync(Bob)).ToursDisabled.Should().BeTrue("the first write keeps it");
        (await _state.GetSnapshotAsync(Bob)).SeenStepIds.Should().BeEquivalentTo(["step-a"]);
    }

    [Fact]
    public async Task AnAccountAddedAfterTheLegacyOptOutStartsWithToursOn()
    {
        await SeedUsersAsync(Alice, Bob);
        await _state.SetToursDisabledAsync(Default, true);
        await SeedUsersCreatedAsync(DateTime.UtcNow.AddHours(1), "user-3");

        (await _state.GetSnapshotAsync("user-3")).ToursDisabled.Should().BeFalse();
        await _state.RecordStepSeenAsync("user-3", "step-a");
        (await _state.GetSnapshotAsync("user-3")).ToursDisabled.Should().BeFalse();
    }

    [Fact]
    public async Task AUserCanTurnTheCarriedOptOutBackOff()
    {
        await SeedUsersAsync(Alice, Bob);
        await _state.SetToursDisabledAsync(Default, true);

        await _state.SetToursDisabledAsync(Bob, false);

        (await _state.GetSnapshotAsync(Bob)).ToursDisabled.Should().BeFalse();
        (await _state.GetSnapshotAsync(Alice)).ToursDisabled.Should().BeTrue();
    }

    // ---- reset ----

    [Fact]
    public async Task ResetRemovesOnlyTheCallersRowAndStillClearsLastSeen()
    {
        await SeedUsersAsync(Alice, Bob);
        await _state.RecordStepSeenAsync(Alice, "step-a");
        await _state.RecordStepSeenAsync(Bob, "step-b");

        await _state.ResetAsync(Alice);

        var alice = await _state.GetSnapshotAsync(Alice);
        alice.SeenStepIds.Should().BeEmpty();
        alice.DismissedTourIds.Should().BeEmpty("a reset re-arms every tour, not just the clean-start baseline");
        (await _state.GetSnapshotAsync(Bob)).SeenStepIds.Should().BeEquivalentTo(["step-b"]);
        (await LastSeenAsync()).Should().BeNull("reset must keep re-arming the automatic offer");
        (await _state.GetFirstSeenVersionAsync()).Should().Be("2.4.0", "FirstSeenVersion cannot be reconstructed");
    }

    [Fact]
    public async Task ResetByTheOnlyAccountTakesTheUnclaimedDefaultRow()
    {
        await _state.RecordToursDismissedAsync(Default, ["tour-x"]);
        await SeedUsersAsync(Alice);

        await _state.ResetAsync(Alice);

        (await SubjectsAsync()).Should().BeEquivalentTo([Alice], "otherwise the next read would claim the history back");
        (await _state.GetSnapshotAsync(Alice)).DismissedTourIds.Should().BeEmpty();
    }

    [Fact]
    public async Task ResetByTheDefaultSubjectRemovesItsRow()
    {
        await _state.RecordToursDismissedAsync(Default, ["tour-x"]);

        await _state.ResetAsync(Default);

        (await SubjectsAsync()).Should().BeEmpty();
    }

    // ---- the clean-start baseline ----

    [Fact]
    public async Task ANewSubjectsFirstOfferHasOnlyCurrentMinorTours()
    {
        await SeedUsersAsync(Alice, Bob);
        await SetFirstSeenAsync(null); // an install older than tours: no floor at all

        var offer = await Tours(Bob).GetDueOfferAsync();

        offer.Should().NotBeNull();
        offer!.TourIds.Should().BeEquivalentTo(new[] { CurrentMinorFirst, CurrentMinorLatest }.Distinct());
        if (CurrentMinorFirst != CurrentMinorLatest)
            offer.Title.Should().Be($"What's new in v{Current.Major}.{Current.Minor}", "a new account never had a last update");
    }

    [Fact]
    public async Task ANewSubjectsFirstWritePersistsTheBaseline()
    {
        await SeedUsersAsync(Alice, Bob);

        await _state.RecordOfferAsync(Bob, [CurrentMinorLatest], Current.ToString());

        (await StoredDismissedAsync(Bob)).Should().BeEquivalentTo([OldTour, OlderPatchTour]);
        (await _state.GetSnapshotAsync(Bob)).DismissedTourIds.Should().BeEquivalentTo([OldTour, OlderPatchTour]);
    }

    [Fact]
    public async Task TheLauncherShowsBaselineToursAsNotSeen()
    {
        await SeedUsersAsync(Alice, Bob);

        var statuses = await Tours(Bob).GetTourStatusesAsync();

        statuses.Single(s => s.Tour.Id == OldTour).Status.Should().Be(TourStatus.NotSeen,
            "the account never touched a baseline tour");
        statuses.Single(s => s.Tour.Id == CurrentMinorLatest).Status.Should().Be(TourStatus.NotSeen);
    }

    [Fact]
    public async Task TheLauncherStillShowsARealSkip()
    {
        await SeedUsersAsync(Alice, Bob);
        await _state.RecordStepSeenAsync(Bob, "step-" + OldTour);
        await _state.RecordToursDismissedAsync(Bob, [OldTour]);
        await _state.RecordStepSeenAsync(Bob, "step-" + CurrentMinorLatest);
        await _state.RecordToursDismissedAsync(Bob, [CurrentMinorLatest]);

        var statuses = await Tours(Bob).GetTourStatusesAsync();

        statuses.Single(s => s.Tour.Id == OldTour).Status.Should().Be(TourStatus.Skipped);
        statuses.Single(s => s.Tour.Id == CurrentMinorLatest).Status.Should().Be(TourStatus.Skipped);
        statuses.Single(s => s.Tour.Id == OlderPatchTour).Status.Should().Be(TourStatus.NotSeen);
    }

    [Fact]
    public async Task TheDefaultSubjectKeepsTheFullBacklog()
    {
        await SetFirstSeenAsync(null);

        (await _state.GetSnapshotAsync(Default)).DismissedTourIds.Should().BeEmpty();
        var offer = await Tours(null, authDisabled: true).GetDueOfferAsync();
        offer!.TourIds.Should().Contain([OldTour, OlderPatchTour]);
        offer.Title.Should().Be("What's new since your last update");
        (await _state.GetSnapshotAsync(Default)).CleanStart.Should().BeFalse();

        await _state.RecordStepSeenAsync(Default, "step-a");
        (await StoredDismissedAsync(Default)).Should().BeEmpty();
    }

    [Fact]
    public async Task AClaimedRowGetsNoBaseline()
    {
        await _state.RecordStepSeenAsync(Default, "step-a");
        await SeedUsersAsync(Alice);

        (await _state.GetSnapshotAsync(Alice)).DismissedTourIds.Should().BeEmpty();
        await _state.RecordStepSeenAsync(Alice, "step-b");
        (await StoredDismissedAsync(Alice)).Should().BeEmpty();
    }

    // ---- helpers ----

    private static TourDefinition Tour(string id) => new()
    {
        Id = id,
        Title = id,
        Steps = [new TourStep { Id = "step-" + id, Url = "/", Selector = "[data-tour=\"x\"]" }],
    };

    /// <summary>A real TourService for <paramref name="userId"/>; predicates and URL tokens stand idle.</summary>
    private TourService Tours(string? userId, bool authDisabled = false)
    {
        var caller = new CallerContext();
        caller.SetUser(authDisabled
            ? CallerInfo.LocalNoAuth(null, null, null)
            : CallerInfo.ForUser(Principal(userId!), null, null, null));
        var site = new NetworkOptimizer.Web.Services.SiteContextService(null!, null!);
        site.UseSite(null);
        return new TourService(_definitions, _state, new IdlePredicates(), site,
            new TourUrlTokenResolver(null!, site, null!, null!, null!, NullLoggerFactory.Instance),
            caller, NullLogger<TourService>.Instance);
    }

    private async Task SetFirstSeenAsync(string? version)
    {
        await using var db = new MainFactory(_mainDbPath).CreateDbContext();
        (await db.AdminSettings.SingleAsync()).FirstSeenVersion = version;
        await db.SaveChangesAsync();
    }

    private async Task<List<string>> StoredDismissedAsync(string subject)
    {
        await using var db = new MainFactory(_mainDbPath).CreateDbContext();
        var json = (await db.TourStates.SingleAsync(t => t.Subject == subject)).DismissedTours;
        return System.Text.Json.JsonSerializer.Deserialize<List<string>>(json)!
            .Select(id => id.StartsWith("dev:", StringComparison.Ordinal) ? id[4..] : id).ToList();
    }

    /// <summary>No step in the test tours requires a predicate, so nothing needs resolving.</summary>
    private sealed class IdlePredicates() : TourPredicateResolver(null!, null!, null!, null!, null!, null!,
        NullLogger<TourPredicateResolver>.Instance)
    {
        public override Task<PredicateContext> ResolveAsync(IReadOnlySet<string> needed) =>
            Task.FromResult(new PredicateContext
            {
                MultiSiteEnabled = false,
                Sites = [],
                QualifyingSites = new(StringComparer.OrdinalIgnoreCase),
                Evaluated = needed,
            });
    }

    private Task SeedUsersAsync(params string[] ids) => SeedUsersCreatedAsync(DateTime.UtcNow.AddHours(-1), ids);

    private async Task SeedUsersCreatedAsync(DateTime createdAt, params string[] ids)
    {
        await using var db = new AuthFactory(_authDbPath).CreateDbContext();
        foreach (var id in ids)
            db.Users.Add(new ApplicationUser
            {
                Id = id,
                UserName = id,
                NormalizedUserName = id.ToUpperInvariant(),
                SecurityStamp = Guid.NewGuid().ToString(),
                CreatedAt = createdAt,
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
