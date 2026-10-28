using FluentAssertions;
using NetworkOptimizer.Web.Services;
using Xunit;

namespace NetworkOptimizer.Web.Tests.Services;

public class SpeedTestFailureSummaryTests
{
    private const string Refused =
        "unable to connect to server - server may have stopped running or use a different port, firewall issue, etc.: Connection refused";

    private static string Json(string error) =>
        "{\n\t\"start\": {\n\t\t\"connected\": [],\n\t\t\"version\": \"iperf 3.17\"\n\t},\n\t\"intervals\": [],\n\t\"end\": {},\n\t\"error\": \"" + error + "\"\n}";

    [Fact]
    public void SameErrorBothDirections_CollapsesToOneShortLine()
    {
        var failure = SpeedTestFailureSummary.Summarize(
            $"Both tests failed. Download: {Json(Refused)}, Upload: {Json(Refused)}");

        failure.Summary.Should().Be("Speed test failed: unable to connect to server (Connection refused)");
        failure.RawOutput.Should().StartWith("Download: {").And.Contain("\n\nUpload: {").And.Contain("Connection refused");
    }

    [Fact]
    public void DifferentErrors_NameEachDirection()
    {
        var failure = SpeedTestFailureSummary.Summarize(
            $"Both tests failed. Download: {Json(Refused)}, Upload: iperf3 client timed out");

        failure.Summary.Should().Be(
            "Speed test failed: From device: unable to connect to server (Connection refused); To device: iperf3 client timed out");
        failure.RawOutput.Should().NotBeNull();
    }

    [Fact]
    public void PlainTextErrors_Collapse()
    {
        var failure = SpeedTestFailureSummary.Summarize(
            "Both tests failed. Download: iperf3 client timed out, Upload: iperf3 client timed out");

        failure.Summary.Should().Be("Speed test failed: iperf3 client timed out");
    }

    [Fact]
    public void SeparatorTextInsideTheDownloadJson_DoesNotSplitIt()
    {
        var download = Json("odd, Upload: text");
        var failure = SpeedTestFailureSummary.Summarize(
            $"Both tests failed. Download: {download}, Upload: {Json(Refused)}");

        failure.Summary.Should().Be(
            "Speed test failed: From device: odd, Upload: text; To device: unable to connect to server (Connection refused)");
    }

    [Theory]
    [InlineData("Cannot connect to device: Authentication failed")]
    [InlineData("Failed to start iperf3 server: permission denied")]
    [InlineData("Both tests failed. Download: {not json")]
    public void OtherMessages_FallBackUnchanged(string message)
    {
        var failure = SpeedTestFailureSummary.Summarize(message);

        failure.Summary.Should().Be($"Speed test failed: {message}");
        failure.RawOutput.Should().BeNull();
    }

    [Fact]
    public void NullMessage_StillReadsAsFailed()
    {
        SpeedTestFailureSummary.Summarize(null).Summary.Should().Be("Speed test failed: ");
    }

    [Theory]
    [InlineData(Refused, "unable to connect to server (Connection refused)")]
    [InlineData("error - unable to connect to server: Connection timed out", "unable to connect to server: Connection timed out")]
    [InlineData("unable to receive control message - port may not be available, the other side may have stopped running, etc.", "unable to receive control message")]
    [InlineData("iperf3 client timed out", "iperf3 client timed out")]
    public void Shorten_KeepsTheCause(string error, string expected)
    {
        SpeedTestFailureSummary.Shorten(error).Should().Be(expected);
    }
}
