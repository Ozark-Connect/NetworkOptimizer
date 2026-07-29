using FluentAssertions;
using Xunit;

namespace NetworkOptimizer.Web.Tests.Identity;

/// <summary>
/// Recovery codes are issued and stored as XXXXX-XXXXX, so the separator is part of the code. It was
/// being stripped before redemption, which meant no recovery code could ever be accepted - the one
/// credential that exists for the case where nothing else works.
/// </summary>
public class RecoveryCodeNormalizationTests
{
    // Mirrors IdentitySignInService.RecoveryCodeSignInAsync.
    private static string Normalize(string recoveryCode)
    {
        var typed = recoveryCode.Replace(" ", string.Empty).Trim().ToUpperInvariant();
        return typed.Length == 10 && !typed.Contains('-') ? typed.Insert(5, "-") : typed;
    }

    [Theory]
    [InlineData("ABCDE-FGHIJ")]           // as issued
    [InlineData("abcde-fghij")]           // typed in lower case
    [InlineData("ABCDEFGHIJ")]            // separator left out
    [InlineData("abcdefghij")]            // both
    [InlineData(" ABCDE-FGHIJ ")]         // padded by a copy/paste
    [InlineData("ABCDE - FGHIJ")]         // spaced around the separator
    public void EveryWayOfTypingOneCode_ReachesTheStoredForm(string typed)
        => Normalize(typed).Should().Be("ABCDE-FGHIJ");

    [Fact]
    public void TheSeparatorSurvives()
        => Normalize("ABCDE-FGHIJ").Should().Contain("-", "the dash is part of the stored code");
}
