using System.Security.Claims;
using FluentAssertions;
using Moq;
using NetworkOptimizer.Storage.Models.Identity;
using NetworkOptimizer.Web.Services;
using NetworkOptimizer.Web.Services.Identity;
using Xunit;

namespace NetworkOptimizer.Web.Tests.Identity;

public class CtiQuotaBannerDismissalTests
{
    private const string Site1 = "site-1";
    private const string Site2 = "site-2";
    private static readonly DateTime Day1 = new(2026, 10, 24, 23, 30, 0, DateTimeKind.Utc);
    private readonly Dictionary<string, string?> _store = new();
    private readonly Mock<ISystemSettingsService> _settings = new();
    private readonly CallerContext _caller = new();

    public CtiQuotaBannerDismissalTests()
    {
        _settings.Setup(s => s.GetGlobalAsync(It.IsAny<string>()))
            .ReturnsAsync((string k) => _store.GetValueOrDefault(k));
        _settings.Setup(s => s.SetGlobalAsync(It.IsAny<string>(), It.IsAny<string?>()))
            .Callback((string k, string? v) => _store[k] = v)
            .Returns(Task.CompletedTask);
    }

    private UserPreferences Prefs() => new(_settings.Object, _caller);

    private void SignIn(string userId)
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, userId), new Claim(ClaimTypes.Role, Roles.Viewer)], "test"));
        _caller.SetUser(CallerInfo.ForUser(principal, null, null, null));
    }

    [Fact]
    public async Task ADismissal_HoldsForTheRestOfThatDay()
    {
        SignIn("user-a");
        await CtiQuotaBannerDismissal.DismissAsync(Prefs(), Site1, Day1.AddHours(-20));

        (await CtiQuotaBannerDismissal.IsDismissedAsync(Prefs(), Site1, Day1)).Should().BeTrue();
    }

    [Fact]
    public async Task ADismissal_EndsWhenTheUtcDayChanges()
    {
        SignIn("user-a");
        await CtiQuotaBannerDismissal.DismissAsync(Prefs(), Site1, Day1);

        (await CtiQuotaBannerDismissal.IsDismissedAsync(Prefs(), Site1, Day1.AddHours(1))).Should().BeFalse();
    }

    [Fact]
    public async Task ADismissal_CoversOnlyThatUserOnThatSite()
    {
        SignIn("user-a");
        await CtiQuotaBannerDismissal.DismissAsync(Prefs(), Site1, Day1);

        (await CtiQuotaBannerDismissal.IsDismissedAsync(Prefs(), Site2, Day1)).Should().BeFalse();
        SignIn("user-b");
        (await CtiQuotaBannerDismissal.IsDismissedAsync(Prefs(), Site1, Day1)).Should().BeFalse();
    }
}
