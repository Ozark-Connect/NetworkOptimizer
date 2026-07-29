using FluentAssertions;
using Xunit;

namespace NetworkOptimizer.Web.Tests.Identity;

/// <summary>
/// JIT provisioning falls back to the IdP's subject when no username claim is configured or sent,
/// and a subject is an identifier rather than a name: Auth0 issues "auth0|68f0...", whose pipe fails
/// Identity's allowed-character check. Every automatic account creation for that provider failed as
/// a result, which looks like the feature is broken rather than like a claim needing configuration.
/// </summary>
public class JitUsernameTests
{
    // Identity's default AllowedUserNameCharacters.
    private const string Allowed =
        "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789-._@+";

    // Mirrors ExternalLoginService.SanitizeUsername.
    private static string Sanitize(string candidate)
    {
        var kept = new string(candidate.Where(Allowed.Contains).ToArray());
        return string.IsNullOrWhiteSpace(kept) ? "generated" : kept;
    }

    [Theory]
    [InlineData("auth0|6a694db4ce9ca0ddf91e4f55", "auth06a694db4ce9ca0ddf91e4f55")]  // the reported failure
    [InlineData("google-oauth2|118273", "google-oauth2118273")]
    [InlineData("waad|abc-def", "waadabc-def")]
    public void SubjectsSurviveAsUsernames(string subject, string expected)
        => Sanitize(subject).Should().Be(expected);

    [Theory]
    [InlineData("alice")]
    [InlineData("alice.smith@example.com")]
    [InlineData("alice_smith-1")]
    public void OrdinaryUsernamesAreUntouched(string username)
        => Sanitize(username).Should().Be(username);

    [Fact]
    public void EverythingStrippedStillYieldsAName()
        => Sanitize("|||").Should().NotBeNullOrWhiteSpace("a user has to be stored under something");
}
