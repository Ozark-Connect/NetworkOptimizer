using System.Text;
using System.Text.RegularExpressions;
using FluentAssertions;
using NetworkOptimizer.Core.Helpers;
using NetworkOptimizer.Web.Services;
using NetworkOptimizer.Web.Services.Ssh;
using Xunit;

namespace NetworkOptimizer.Web.Tests.Services;

/// <summary>
/// Gateways exec what we write to /data/on_boot.d/ and parse what we write to
/// /etc/systemd/system/, and neither treats a carriage return as whitespace. C# carries the build
/// host's line endings into raw string literals, verbatim strings and StringBuilder.AppendLine
/// alike, so a Windows build emits CRLF where a Linux build emits LF unless the encode step
/// normalizes.
/// </summary>
public class GatewayScriptLineEndingTests
{
    [Fact]
    public void WanSteerBootScript_EncodesWithoutCarriageReturns()
    {
        var decoded = Decode(GatewayFile.ToBase64(WanSteerDeploymentService.GenerateBootScript()));

        decoded.Should().NotContain("\r");
        decoded.Split('\n')[0].Should().Be("#!/bin/sh");
    }

    [Fact]
    public void UdmBootUnit_EncodesWithoutCarriageReturns()
    {
        var decoded = Decode(GatewayFile.ToBase64(UdmBootService.ServiceUnitContent));

        decoded.Should().NotContain("\r");
        decoded.Split('\n')[0].Should().Be("[Unit]");
    }

    /// <summary>
    /// The guard that actually prevents a repeat. WAN Steering was missed because it was written
    /// after the two services that normalized inline, and nothing failed when it did not. Any file
    /// under Services/ that pipes content through "base64 -d" on a gateway is a deployment path, and
    /// must encode through <see cref="GatewayFile"/> rather than rolling its own.
    /// </summary>
    [Fact]
    public void EveryGatewayWritePath_EncodesThroughGatewayFile()
    {
        // Raw text encoding: Convert.ToBase64String(...GetBytes(...)). Binary payloads (a kernel
        // module read as bytes) are not text and must not be line-ending normalized, so they take
        // the byte[] overload and do not match this pattern.
        var rawTextEncode = new Regex(@"Convert\.ToBase64String\([^;]*GetBytes\(", RegexOptions.Compiled);

        var offenders = new List<string>();
        foreach (var file in Directory.EnumerateFiles(FindServicesRoot(), "*.cs", SearchOption.AllDirectories))
        {
            var source = File.ReadAllText(file);
            if (!source.Contains("base64 -d", StringComparison.Ordinal))
                continue;

            if (rawTextEncode.IsMatch(source))
                offenders.Add(Path.GetFileName(file));
        }

        offenders.Should().BeEmpty(
            "gateway-bound text must be encoded with GatewayFile.ToBase64 so the bytes on the "
            + "gateway do not depend on whether the build ran on Windows or Linux");
    }

    private static string Decode(string base64) =>
        Encoding.UTF8.GetString(Convert.FromBase64String(base64));

    private static string FindServicesRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "src", "NetworkOptimizer.Web", "Services");
            if (Directory.Exists(candidate))
                return candidate;
            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate src/NetworkOptimizer.Web/Services from the test output.");
    }
}
