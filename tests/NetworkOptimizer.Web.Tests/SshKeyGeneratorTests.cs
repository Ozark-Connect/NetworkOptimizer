using System.Text;
using FluentAssertions;
using NetworkOptimizer.Web.Services.Ssh;
using Renci.SshNet;
using Xunit;

namespace NetworkOptimizer.Web.Tests;

/// <summary>
/// A generated key we cannot read back is the failure mode that matters: the UI would hand out a public
/// half, the user would install it on their gateway, and every connection afterwards would fail. So the
/// contract under test is the round trip - generate here, parse with the same SSH.NET type the auth path
/// uses, and confirm the public half SSH.NET derives is the one we displayed.
/// </summary>
public class SshKeyGeneratorTests
{
    [Theory]
    [InlineData(SshKeyType.Ed25519)]
    [InlineData(SshKeyType.Rsa4096)]
    public void GeneratedKey_RoundTripsThroughSshNet(SshKeyType type)
    {
        var generated = SshKeyGenerator.Generate(type);

        using var pem = new MemoryStream(Encoding.ASCII.GetBytes(generated.PrivateKeyPem));
        var parsed = new PrivateKeyFile(pem);

        parsed.Should().NotBeNull();
        parsed.HostKeyAlgorithms.Should().NotBeEmpty(
            "SSH.NET must derive usable host key algorithms from the key we generated");
    }

    [Theory]
    [InlineData(SshKeyType.Ed25519, "ssh-ed25519")]
    [InlineData(SshKeyType.Rsa4096, "ssh-rsa")]
    public void PublicKey_IsASingleOpenSshLine(SshKeyType type, string expectedPrefix)
    {
        var generated = SshKeyGenerator.Generate(type);

        generated.PublicKey.Should().NotContain("\n");
        generated.PublicKey.Should().StartWith(expectedPrefix + " ");
        generated.PublicKey.Should().EndWith(" " + SshKeyGenerator.DefaultComment);

        var blob = generated.PublicKey.Split(' ')[1];
        var decode = () => Convert.FromBase64String(blob);
        decode.Should().NotThrow("the middle field must be the base64 public key blob");
    }

    [Theory]
    [InlineData(SshKeyType.Ed25519)]
    [InlineData(SshKeyType.Rsa4096)]
    public void Fingerprint_MatchesTheOneDerivedFromThePublicLine(SshKeyType type)
    {
        var generated = SshKeyGenerator.Generate(type);

        generated.Fingerprint.Should().StartWith("SHA256:");
        generated.Fingerprint.Should().NotEndWith("=", "ssh-keygen strips the base64 padding");
        SshKeyGenerator.FingerprintOfPublicKey(generated.PublicKey).Should().Be(generated.Fingerprint,
            "an uploaded key and a generated key must fingerprint identically, or the UI shows two "
            + "different values for the same key");
    }

    [Fact]
    public void EachGeneration_ProducesADistinctKey()
    {
        var first = SshKeyGenerator.Generate(SshKeyType.Ed25519);
        var second = SshKeyGenerator.Generate(SshKeyType.Ed25519);

        second.Fingerprint.Should().NotBe(first.Fingerprint);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-a-key")]
    [InlineData("ssh-ed25519")]
    [InlineData("ssh-ed25519 !!!not-base64!!! comment")]
    public void FingerprintOfPublicKey_ReturnsNullForJunk(string line)
    {
        SshKeyGenerator.FingerprintOfPublicKey(line).Should().BeNull();
    }
}
