using System.Security.Claims;
using FluentAssertions;
using Moq;
using NetworkOptimizer.Storage.Models.Identity;
using NetworkOptimizer.Web.Services;
using NetworkOptimizer.Web.Services.Identity;
using Xunit;

namespace NetworkOptimizer.Web.Tests.Identity;

public class ThreatFilterPreferenceTests
{
    private const string Site1 = "site-1";
    private const string Site2 = "site-2";
    private readonly Dictionary<string, string?> _store = new();
    private readonly Mock<ISystemSettingsService> _settings = new();
    private readonly CallerContext _caller = new();

    public ThreatFilterPreferenceTests()
    {
        _settings.Setup(s => s.GetGlobalAsync(It.IsAny<string>()))
            .ReturnsAsync((string k) => _store.GetValueOrDefault(k));
        _settings.Setup(s => s.SetGlobalAsync(It.IsAny<string>(), It.IsAny<string?>()))
            .Callback((string k, string? v) => _store[k] = v)
            .Returns(Task.CompletedTask);
    }

    private UserPreferences Prefs() => new(_settings.Object, _caller);

    private Task<bool> LoadAsync(string site) => ThreatFilterPreference.LoadAsync(
        Prefs(), site, () => Task.FromResult(_store.GetValueOrDefault(ThreatFilterPreference.LegacyKey)));

    private void SignIn(string userId)
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, userId), new Claim(ClaimTypes.Role, Roles.Viewer)], "test"));
        _caller.SetUser(CallerInfo.ForUser(principal, null, null, null));
    }

    [Fact]
    public async Task AToggle_ChangesOnlyThatUserOnThatSite()
    {
        SignIn("user-a");
        await ThreatFilterPreference.SaveAsync(Prefs(), Site1, active: false);

        (await LoadAsync(Site1)).Should().BeFalse();
        (await LoadAsync(Site2)).Should().BeTrue("user A's choice on site 1 does not apply on site 2");

        SignIn("user-b");
        (await LoadAsync(Site1)).Should().BeTrue("user A's choice does not change user B's view");

        _store.ContainsKey(ThreatFilterPreference.LegacyKey).Should().BeFalse();
    }

    [Fact]
    public async Task WithNoValueOfTheirOwn_TheLegacyInstallWideValueIsRead()
    {
        _store[ThreatFilterPreference.LegacyKey] = "false";
        SignIn("user-a");

        (await LoadAsync(Site1)).Should().BeFalse();
    }

    [Fact]
    public async Task WithNeitherValue_FiltersAreOn()
    {
        SignIn("user-a");

        (await LoadAsync(Site1)).Should().BeTrue();
    }

    [Fact]
    public async Task AUsersOwnValueWinsOverTheLegacyValue()
    {
        _store[ThreatFilterPreference.LegacyKey] = "false";
        SignIn("user-a");
        await ThreatFilterPreference.SaveAsync(Prefs(), Site1, active: true);

        (await LoadAsync(Site1)).Should().BeTrue();
    }
}
