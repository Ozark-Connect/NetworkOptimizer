using System.Security.Claims;
using FluentAssertions;
using Moq;
using NetworkOptimizer.Storage.Models.Identity;
using NetworkOptimizer.Web.Services;
using NetworkOptimizer.Web.Services.Identity;
using Xunit;

namespace NetworkOptimizer.Web.Tests.Identity;

public class UserDismissalsTests
{
    private const string Key = "ui.test_banner_dismissed";
    private readonly Dictionary<string, string?> _store = new();
    private readonly Mock<ISystemSettingsService> _settings = new();
    private readonly CallerContext _caller = new();

    public UserDismissalsTests()
    {
        _settings.Setup(s => s.GetGlobalAsync(It.IsAny<string>()))
            .ReturnsAsync((string k) => _store.GetValueOrDefault(k));
        _settings.Setup(s => s.SetGlobalAsync(It.IsAny<string>(), It.IsAny<string?>()))
            .Callback((string k, string? v) => _store[k] = v)
            .Returns(Task.CompletedTask);
    }

    private UserDismissals Sut() => new(_settings.Object, _caller);

    private void SignIn(string userId, string role)
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, userId), new Claim(ClaimTypes.Role, role)], "test"));
        _caller.SetUser(CallerInfo.ForUser(principal, null, null, null));
    }

    [Fact]
    public async Task Dismissal_IsPerUser()
    {
        SignIn("user-1", Roles.Viewer);
        await Sut().DismissAsync(Key);

        (await Sut().IsDismissedAsync(Key)).Should().BeTrue();
        SignIn("user-2", Roles.Viewer);
        (await Sut().IsDismissedAsync(Key)).Should().BeFalse();
        _store.Should().NotContainKey(Key);
    }

    [Fact]
    public async Task InstallWideFlag_CarriesToAdmins_NotOtherRoles()
    {
        _store[Key] = "true";

        SignIn("admin", Roles.Admin);
        (await Sut().IsDismissedAsync(Key)).Should().BeTrue();
        SignIn("viewer", Roles.Viewer);
        (await Sut().IsDismissedAsync(Key)).Should().BeFalse();
    }

    [Fact]
    public async Task AuthenticationDisabled_UsesInstallWideFlag()
    {
        _caller.SetUser(CallerInfo.LocalNoAuth(null, null, null));
        await Sut().DismissAsync(Key);

        _store[Key].Should().Be("true");
        (await Sut().IsDismissedAsync(Key)).Should().BeTrue();
    }

    [Fact]
    public async Task Anonymous_NeitherReadsNorWrites()
    {
        _store[Key] = "true";
        _caller.SetUser(CallerInfo.Anonymous(null, null, null, null));

        (await Sut().IsDismissedAsync(Key)).Should().BeFalse();
        await Sut().DismissAsync(Key);
        _settings.Verify(s => s.SetGlobalAsync(It.IsAny<string>(), It.IsAny<string?>()), Times.Never);
    }

    [Fact]
    public async Task LegacyPresenceValue_CountsAsDismissed()
    {
        _store[Key] = "1";
        SignIn("admin", Roles.Admin);

        (await Sut().IsDismissedAsync(Key)).Should().BeTrue();
    }
}
