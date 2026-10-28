using System.Security.Claims;
using FluentAssertions;
using Moq;
using NetworkOptimizer.Storage.Models.Identity;
using NetworkOptimizer.Web.Services;
using NetworkOptimizer.Web.Services.Identity;
using Xunit;

namespace NetworkOptimizer.Web.Tests.Identity;

public class LanDeviceFilterPreferenceTests
{
    private const string Site1 = "site-1";
    private const string Site2 = "site-2";
    private readonly Dictionary<string, string?> _store = new();
    private readonly Mock<ISystemSettingsService> _settings = new();
    private readonly CallerContext _caller = new();

    public LanDeviceFilterPreferenceTests()
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
    public async Task NothingStored_IsAllModelsAndEveryDevice()
    {
        SignIn("user-1");

        var (family, onlineOnly) = await LanDeviceFilterPreference.LoadAsync(Prefs(), Site1);

        family.Should().BeEmpty();
        onlineOnly.Should().BeFalse();
    }

    [Fact]
    public async Task Saved_RoundTrips()
    {
        SignIn("user-1");
        await LanDeviceFilterPreference.SaveFamilyAsync(Prefs(), Site1, "U7-Pro");
        await LanDeviceFilterPreference.SaveOnlineOnlyAsync(Prefs(), Site1, true);

        var (family, onlineOnly) = await LanDeviceFilterPreference.LoadAsync(Prefs(), Site1);

        family.Should().Be("U7-Pro");
        onlineOnly.Should().BeTrue();
    }

    [Fact]
    public async Task OneSitesFilter_DoesNotReachAnother()
    {
        SignIn("user-1");
        await LanDeviceFilterPreference.SaveFamilyAsync(Prefs(), Site1, "U7-Pro");
        await LanDeviceFilterPreference.SaveOnlineOnlyAsync(Prefs(), Site1, true);

        var (family, onlineOnly) = await LanDeviceFilterPreference.LoadAsync(Prefs(), Site2);

        family.Should().BeEmpty();
        onlineOnly.Should().BeFalse();
    }

    [Fact]
    public async Task OneUsersFilter_DoesNotReachAnother()
    {
        SignIn("user-1");
        await LanDeviceFilterPreference.SaveFamilyAsync(Prefs(), Site1, "U7-Pro");

        SignIn("user-2");
        var (family, _) = await LanDeviceFilterPreference.LoadAsync(Prefs(), Site1);

        family.Should().BeEmpty();
    }
}
