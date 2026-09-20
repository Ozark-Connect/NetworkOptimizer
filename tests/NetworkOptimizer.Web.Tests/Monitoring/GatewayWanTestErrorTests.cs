using FluentAssertions;
using NetworkOptimizer.Web.Services;
using Xunit;

namespace NetworkOptimizer.Web.Tests.Monitoring;

/// <summary>
/// The gateway binary exits non-zero and prints its failure as JSON. The stored error is that
/// JSON's error text, not the whole document.
/// </summary>
public class GatewayWanTestErrorTests
{
    [Fact]
    public void TheBinarysJsonErrorIsReadOutOfTheDocument()
    {
        var output = """
            {
              "success": false,
              "error": "token: fetch token: context deadline exceeded",
              "timestamp": "2026-01-01T00:00:00Z"
            }
            """;

        GatewayWanSpeedTestService.ExtractBinaryError(output)
            .Should().Be("token: fetch token: context deadline exceeded");
    }

    [Fact]
    public void OutputThatIsNotJsonIsKeptAsIs()
    {
        GatewayWanSpeedTestService.ExtractBinaryError("sh: /data/uwnspeedtest: not found\n")
            .Should().Be("sh: /data/uwnspeedtest: not found");
    }

    [Fact]
    public void JsonWithNoErrorTextIsKeptAsIs()
    {
        GatewayWanSpeedTestService.ExtractBinaryError("""{"success":false}""")
            .Should().Be("""{"success":false}""");
    }

    [Fact]
    public void EmptyOutputStaysEmpty()
    {
        GatewayWanSpeedTestService.ExtractBinaryError("").Should().BeEmpty();
    }
}
