using System.Security.Claims;
using FluentAssertions;
using Moq;
using NetworkOptimizer.Storage.Models.Identity;
using NetworkOptimizer.Web.Services;
using NetworkOptimizer.Web.Services.Identity;
using Xunit;

namespace NetworkOptimizer.Web.Tests.Identity;

public class UserPreferencesTests
{
    private const string Key = "ui.test_layout";
    private readonly Dictionary<string, string?> _store = new();
    private readonly Mock<ISystemSettingsService> _settings = new();
    private readonly CallerContext _caller = new();

    public UserPreferencesTests()
    {
        _settings.Setup(s => s.GetGlobalAsync(It.IsAny<string>()))
            .ReturnsAsync((string k) => _store.GetValueOrDefault(k));
        _settings.Setup(s => s.SetGlobalAsync(It.IsAny<string>(), It.IsAny<string?>()))
            .Callback((string k, string? v) => _store[k] = v)
            .Returns(Task.CompletedTask);
    }

    private UserPreferences Sut() => new(_settings.Object, _caller);

    private void SignIn(string userId)
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, userId), new Claim(ClaimTypes.Role, Roles.Viewer)], "test"));
        _caller.SetUser(CallerInfo.ForUser(principal, null, null, null));
    }

    [Fact]
    public async Task Value_IsPerUser_AndLeavesTheInstallWideValueAlone()
    {
        _store[Key] = "install";
        SignIn("user-1");
        await Sut().SetAsync(Key, "mine");

        (await Sut().GetAsync(Key)).Should().Be("mine");
        SignIn("user-2");
        (await Sut().GetAsync(Key)).Should().Be("install");
        _store[Key].Should().Be("install");
    }

    [Fact]
    public async Task AuthenticationDisabled_UsesInstallWideValue()
    {
        _caller.SetUser(CallerInfo.LocalNoAuth(null, null, null));
        await Sut().SetAsync(Key, "local");

        _store[Key].Should().Be("local");
        (await Sut().GetAsync(Key)).Should().Be("local");
    }

    [Fact]
    public async Task Anonymous_ReadsInstallWideValue_AndWritesNothing()
    {
        _store[Key] = "install";
        _caller.SetUser(CallerInfo.Anonymous(null, null, null, null));

        (await Sut().GetAsync(Key)).Should().Be("install");
        await Sut().SetAsync(Key, "x");
        _settings.Verify(s => s.SetGlobalAsync(It.IsAny<string>(), It.IsAny<string?>()), Times.Never);
    }
}
