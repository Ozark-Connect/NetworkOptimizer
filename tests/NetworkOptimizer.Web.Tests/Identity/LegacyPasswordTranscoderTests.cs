using FluentAssertions;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using NetworkOptimizer.Storage.Models.Identity;
using NetworkOptimizer.Web.Services;
using NetworkOptimizer.Web.Services.Identity;
using Xunit;

namespace NetworkOptimizer.Web.Tests.Identity;

/// <summary>
/// Proves the one-time migration hash transcode is correct: a legacy PBKDF2 hash, reframed into
/// Identity's V3 layout, must verify against the REAL <see cref="PasswordHasher{TUser}"/> with the
/// original plaintext. A failure here would lock the migrated admin out (design doc 02, migration
/// step 2 - "a unit test MUST prove transcoded output verifies against the real PasswordHasher").
/// </summary>
public class LegacyPasswordTranscoderTests
{
    // Match the app's configured strength so a transcoded hash isn't flagged for rehash purely on
    // iteration count (PRF still differs - see the SuccessRehashNeeded assertion below).
    private static PasswordHasher<ApplicationUser> RealHasher()
        => new(Options.Create(new PasswordHasherOptions { IterationCount = 600_000 }));

    [Theory]
    [InlineData("correct horse battery staple")]
    [InlineData("P@ssw0rd!")]
    [InlineData("a")]
    [InlineData("unicode-café-über-😀")]
    public void TranscodedLegacyHash_VerifiesAgainstRealIdentityHasher(string password)
    {
        // Arrange: produce a legacy-format hash exactly as the app historically stored it.
        var legacyHasher = new PasswordHasher();
        var legacyHash = legacyHasher.HashPassword(password);
        LegacyPasswordTranscoder.IsLegacyFormat(legacyHash).Should().BeTrue();

        // Act: transcode to Identity V3 (no plaintext used).
        var v3Hash = LegacyPasswordTranscoder.TranscodeToIdentityV3(legacyHash);

        // Assert: the real Identity hasher accepts the correct password.
        var real = RealHasher();
        var result = real.VerifyHashedPassword(new ApplicationUser(), v3Hash, password);
        result.Should().BeOneOf(
            PasswordVerificationResult.Success,
            PasswordVerificationResult.SuccessRehashNeeded);
    }

    [Fact]
    public void TranscodedHash_RejectsWrongPassword()
    {
        var legacyHash = new PasswordHasher().HashPassword("the-real-password");
        var v3Hash = LegacyPasswordTranscoder.TranscodeToIdentityV3(legacyHash);

        var result = RealHasher().VerifyHashedPassword(new ApplicationUser(), v3Hash, "wrong-password");

        result.Should().Be(PasswordVerificationResult.Failed);
    }

    [Fact]
    public void Transcode_IsByteStableFramingOfTheSameKeyMaterial()
    {
        // The transcode must be deterministic framing (same input -> same output), since it runs
        // once against a fixed stored hash.
        var legacyHash = new PasswordHasher().HashPassword("stable");
        LegacyPasswordTranscoder.TranscodeToIdentityV3(legacyHash)
            .Should().Be(LegacyPasswordTranscoder.TranscodeToIdentityV3(legacyHash));
    }

    [Fact]
    public void IsLegacyFormat_DistinguishesLegacyFromV3()
    {
        var legacyHash = new PasswordHasher().HashPassword("x");
        var v3Hash = LegacyPasswordTranscoder.TranscodeToIdentityV3(legacyHash);

        LegacyPasswordTranscoder.IsLegacyFormat(legacyHash).Should().BeTrue();
        LegacyPasswordTranscoder.IsLegacyFormat(v3Hash).Should().BeFalse(
            "Identity V3 hashes are dot-free base64 blobs");
        LegacyPasswordTranscoder.IsLegacyFormat(null).Should().BeFalse();
        LegacyPasswordTranscoder.IsLegacyFormat("").Should().BeFalse();
    }

    [Fact]
    public void LegacyFallbackHasher_AcceptsLegacyHash_AndSignalsRehash()
    {
        // The belt-and-suspenders runtime shim must verify a still-legacy-format stored hash and
        // ask Identity to rehash it to V3.
        var legacyHash = new PasswordHasher().HashPassword("legacy-login");
        var shim = new LegacyFallbackPasswordHasher();

        shim.VerifyHashedPassword(new ApplicationUser(), legacyHash, "legacy-login")
            .Should().Be(PasswordVerificationResult.SuccessRehashNeeded);
        shim.VerifyHashedPassword(new ApplicationUser(), legacyHash, "nope")
            .Should().Be(PasswordVerificationResult.Failed);
    }
}
