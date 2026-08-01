using System.Text;
using FluentAssertions;
using NetworkOptimizer.Core.Helpers;
using Xunit;

namespace NetworkOptimizer.Core.Tests.Helpers;

/// <summary>
/// These build their input with explicit \r\n rather than relying on the source file's own line
/// endings, so they assert the same thing on a Linux CI agent and a Windows dev box. A test that
/// checked "the generated script has no CR" would pass vacuously on Linux and only fail on Windows,
/// which is exactly how the CRLF gap survived in the first place.
/// </summary>
public class GatewayFileTests
{
    [Fact]
    public void ToUnixText_CollapsesCrlfToLf()
    {
        GatewayFile.ToUnixText("#!/bin/sh\r\necho hi\r\n").Should().Be("#!/bin/sh\necho hi\n");
    }

    [Fact]
    public void ToUnixText_CollapsesLoneCrToLf()
    {
        GatewayFile.ToUnixText("#!/bin/sh\recho hi").Should().Be("#!/bin/sh\necho hi");
    }

    [Fact]
    public void ToUnixText_LeavesLfContentByteIdentical()
    {
        // The whole reason this change is safe to ship without gateway testing: every install that
        // works today is built on Linux and already emits LF, so normalization is a no-op for them.
        const string alreadyUnix = "#!/bin/sh\necho hi\n";
        GatewayFile.ToUnixText(alreadyUnix).Should().BeSameAs(alreadyUnix);
    }

    [Fact]
    public void ToUnixText_IsIdempotent()
    {
        var once = GatewayFile.ToUnixText("a\r\nb\rc\n");
        GatewayFile.ToUnixText(once).Should().Be(once);
    }

    [Fact]
    public void ToBase64_DecodesWithoutCarriageReturns()
    {
        var decoded = Decode(GatewayFile.ToBase64("#!/bin/sh\r\necho hi\r\n"));

        decoded.Should().NotContain("\r");
        decoded.Should().StartWith("#!/bin/sh\n");
    }

    [Fact]
    public void ToBase64_MatchesPlainEncodingForLfContent()
    {
        const string alreadyUnix = "[Unit]\nDescription=x\n";
        var plain = Convert.ToBase64String(Encoding.UTF8.GetBytes(alreadyUnix));

        GatewayFile.ToBase64(alreadyUnix).Should().Be(plain);
    }

    [Fact]
    public void ToBase64_ShebangSurvivesAsExecutable()
    {
        // A CR here is not cosmetic: the kernel would look for an interpreter named "/bin/sh\r".
        var decoded = Decode(GatewayFile.ToBase64("#!/bin/sh\r\n# comment\r\nexit 0\r\n"));

        decoded.Split('\n')[0].Should().Be("#!/bin/sh");
    }

    private static string Decode(string base64) =>
        Encoding.UTF8.GetString(Convert.FromBase64String(base64));
}
