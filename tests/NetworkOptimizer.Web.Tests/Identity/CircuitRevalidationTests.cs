using System.Reflection;
using System.Security.Claims;
using FluentAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NetworkOptimizer.Storage.Models.Identity;
using NetworkOptimizer.Web.Services.Identity;
using Xunit;

namespace NetworkOptimizer.Web.Tests.Identity;

/// <summary>
/// A live circuit's revalidation tick: a stale security stamp or membership version is asked to
/// refresh once, and a circuit that is still stale on the next tick is ended.
/// </summary>
public sealed class CircuitRevalidationTests : IAsyncLifetime
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"netopt-reval-test-{Guid.NewGuid():N}.db");
    private ServiceProvider _provider = null!;
    private string _userId = "";

    private sealed class RecordingNavigationManager : NavigationManager
    {
        public List<string> Navigations { get; } = new();
        public RecordingNavigationManager() => Initialize("http://localhost/", "http://localhost/monitoring");
        protected override void NavigateToCore(string uri, NavigationOptions options) => Navigations.Add(uri);
    }

    public async Task InitializeAsync()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDataProtection();
        services.AddNetOptIdentityCore(_dbPath);
        services.AddSingleton<EndedSessionStore>();
        _provider = services.BuildServiceProvider();

        using var scope = _provider.CreateScope();
        await scope.ServiceProvider.GetRequiredService<AuthDbContext>().Database.MigrateAsync();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var user = new ApplicationUser { UserName = "User1", IsEnabled = true };
        (await users.CreateAsync(user)).Succeeded.Should().BeTrue();
        _userId = user.Id;
    }

    public async Task DisposeAsync()
    {
        await _provider.DisposeAsync();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { File.Delete(_dbPath); } catch { /* temp file; a leftover is harmless */ }
    }

    private (RevalidatingIdentityAuthenticationStateProvider Provider, RecordingNavigationManager Navigation) NewCircuit()
    {
        var navigation = new RecordingNavigationManager();
        var provider = new RevalidatingIdentityAuthenticationStateProvider(
            NullLoggerFactory.Instance,
            _provider.GetRequiredService<IServiceScopeFactory>(),
            _provider.GetRequiredService<IOptions<IdentityOptions>>(),
            navigation);
        return (provider, navigation);
    }

    /// <summary>The principal a circuit captured: the user's stamp and membership version at that moment.</summary>
    private async Task<ClaimsPrincipal> CapturePrincipalAsync()
    {
        using var scope = _provider.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var options = _provider.GetRequiredService<IOptions<IdentityOptions>>().Value;
        var user = (await users.FindByIdAsync(_userId))!;
        var identity = new ClaimsIdentity(new[]
        {
            new Claim(options.ClaimsIdentity.UserIdClaimType, user.Id),
            new Claim(options.ClaimsIdentity.UserNameClaimType, user.UserName!),
            new Claim(options.ClaimsIdentity.SecurityStampClaimType, (await users.GetSecurityStampAsync(user))!),
            new Claim(NetOptClaims.MembershipVersion, user.MembershipVersion.ToString()),
        }, "Identity.Application");
        return new ClaimsPrincipal(identity);
    }

    private async Task RotateStampAsync()
    {
        using var scope = _provider.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        (await users.UpdateSecurityStampAsync((await users.FindByIdAsync(_userId))!)).Succeeded.Should().BeTrue();
    }

    private async Task SetStampAsync(string stamp)
    {
        using var scope = _provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
        var user = await db.Users.SingleAsync(u => u.Id == _userId);
        user.SecurityStamp = stamp;
        await db.SaveChangesAsync();
    }

    private async Task<string> CurrentStampAsync()
    {
        using var scope = _provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
        return (await db.Users.AsNoTracking().SingleAsync(u => u.Id == _userId)).SecurityStamp!;
    }

    private async Task BumpMembershipAsync()
    {
        using var scope = _provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
        var user = await db.Users.SingleAsync(u => u.Id == _userId);
        user.MembershipVersion++;
        await db.SaveChangesAsync();
    }

    private static readonly MethodInfo Validate = typeof(RevalidatingIdentityAuthenticationStateProvider)
        .GetMethod("ValidateAuthenticationStateAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;

    private static Task<bool> TickAsync(RevalidatingIdentityAuthenticationStateProvider provider, ClaimsPrincipal principal) =>
        (Task<bool>)Validate.Invoke(provider, new object[] { new AuthenticationState(principal), CancellationToken.None })!;

    // --- Pins: behavior that holds before and after ---

    [Fact]
    public async Task MatchingStampAndMembership_StaysValid_WithNoNavigation()
    {
        var principal = await CapturePrincipalAsync();
        var (provider, navigation) = NewCircuit();

        (await TickAsync(provider, principal)).Should().BeTrue();
        (await TickAsync(provider, principal)).Should().BeTrue();
        navigation.Navigations.Should().BeEmpty();
    }

    [Fact]
    public async Task StaleStamp_FirstTick_AsksForRevalidationAndStaysValid()
    {
        var principal = await CapturePrincipalAsync();
        await RotateStampAsync();
        var (provider, navigation) = NewCircuit();

        (await TickAsync(provider, principal)).Should().BeTrue();
        navigation.Navigations.Should().ContainSingle()
            .Which.Should().Contain("/api/account/revalidate?returnUrl=%2Fmonitoring");
    }

    [Fact]
    public async Task StaleMembership_AskedOnce_ThenEndedOnTheSecondTick()
    {
        var principal = await CapturePrincipalAsync();
        await BumpMembershipAsync();
        var (provider, navigation) = NewCircuit();

        (await TickAsync(provider, principal)).Should().BeTrue();
        (await TickAsync(provider, principal)).Should().BeFalse();
        navigation.Navigations.Should().ContainSingle().Which.Should().Contain("/api/account/session?returnUrl=");
    }

    [Fact]
    public async Task DisabledAccount_IsEnded()
    {
        var principal = await CapturePrincipalAsync();
        using (var scope = _provider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
            (await db.Users.SingleAsync(u => u.Id == _userId)).IsEnabled = false;
            await db.SaveChangesAsync();
        }
        var (provider, _) = NewCircuit();

        (await TickAsync(provider, principal)).Should().BeFalse();
    }

    // --- New: the stamp refresh is asked once ---

    [Fact]
    public async Task StaleStamp_StillStaleOnTheSecondTick_IsEnded()
    {
        var principal = await CapturePrincipalAsync();
        await RotateStampAsync();
        var (provider, navigation) = NewCircuit();

        (await TickAsync(provider, principal)).Should().BeTrue();
        (await TickAsync(provider, principal)).Should().BeFalse();
        navigation.Navigations.Should().ContainSingle();
    }

    [Fact]
    public async Task StaleStamp_ThenMatch_ResetsTheRequest()
    {
        var principal = await CapturePrincipalAsync();
        var original = await CurrentStampAsync();
        var (provider, navigation) = NewCircuit();

        await SetStampAsync("rotated-1");
        (await TickAsync(provider, principal)).Should().BeTrue();

        await SetStampAsync(original);
        (await TickAsync(provider, principal)).Should().BeTrue();

        await SetStampAsync("rotated-2");
        (await TickAsync(provider, principal)).Should().BeTrue("the earlier request was answered by a match");
        navigation.Navigations.Should().HaveCount(2);
    }

    [Fact]
    public async Task StaleStampRequest_DoesNotConsumeTheMembershipRequest()
    {
        var principal = await CapturePrincipalAsync();
        var original = await CurrentStampAsync();
        var (provider, _) = NewCircuit();

        await SetStampAsync("rotated-1");
        (await TickAsync(provider, principal)).Should().BeTrue();

        await SetStampAsync(original);
        await BumpMembershipAsync();
        (await TickAsync(provider, principal)).Should().BeTrue("the membership branch gets its own first request");
    }
}
