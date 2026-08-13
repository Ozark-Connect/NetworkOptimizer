using System.Net;
using System.Net.Sockets;
using FluentAssertions;
using NetworkOptimizer.Web.Services.Monitoring;
using Xunit;

namespace NetworkOptimizer.Web.Tests;

/// <summary>
/// The line this produces is what the stats panel and the Settings test button show, and it backs
/// every HTTP device provider. The point of each case is that the reader can tell an unreachable
/// device from one refusing their password from one answering with something unreadable.
/// </summary>
public class HttpFailureSummaryTests
{
    private const string Host = "192.0.2.10";

    private static HttpRequestException WithStatus(HttpStatusCode status)
        => new("request failed", null, status);

    private static HttpRequestException WithSocket(SocketError error)
        => new("request failed", new SocketException((int)error));

    // A client timeout surfaces as TaskCanceledException, which derives from
    // OperationCanceledException, so both must land on the timeout line rather than the
    // catch-all.
    [Fact]
    public void Describe_TaskCanceled_ReadsAsTimeout()
    {
        HttpFailureSummary.Describe(new TaskCanceledException(), Host)
            .Should().Be($"{Host} did not answer in time. Check connectivity or firewall rules.");
    }

    [Fact]
    public void Describe_OperationCanceled_ReadsAsTimeout()
    {
        HttpFailureSummary.Describe(new OperationCanceledException(), Host)
            .Should().Be($"{Host} did not answer in time. Check connectivity or firewall rules.");
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    public void Describe_AuthStatuses_PointAtTheSavedCredentials(HttpStatusCode status)
    {
        HttpFailureSummary.Describe(WithStatus(status), Host)
            .Should().Be($"{Host} rejected the sign-in. Check the username and password saved for it.");
    }

    // A 404 means something answered, so the address is right and the model setting is not.
    [Fact]
    public void Describe_NotFound_PointsAtTheModelSetting()
    {
        HttpFailureSummary.Describe(WithStatus(HttpStatusCode.NotFound), Host)
            .Should().Be($"{Host} answered, but not on the address this model is polled at. Check the model setting.");
    }

    [Theory]
    [InlineData(HttpStatusCode.RequestTimeout)]
    [InlineData(HttpStatusCode.GatewayTimeout)]
    public void Describe_TimeoutStatuses_ReadAsTimeout(HttpStatusCode status)
    {
        HttpFailureSummary.Describe(WithStatus(status), Host).Should().Be($"{Host} did not answer in time.");
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError, 500)]
    [InlineData(HttpStatusCode.ServiceUnavailable, 503)]
    public void Describe_ServerErrors_BlameTheDevice(HttpStatusCode status, int code)
    {
        HttpFailureSummary.Describe(WithStatus(status), Host)
            .Should().Be($"{Host} answered with an error of its own ({code}). It may be starting up or overloaded.");
    }

    [Fact]
    public void Describe_OtherStatus_NamesTheCode()
    {
        HttpFailureSummary.Describe(WithStatus(HttpStatusCode.BadRequest), Host)
            .Should().Be($"{Host} answered 400, which is not a reply this optimizer can use.");
    }

    // Refused and unresolved are the difference between a wrong port and a wrong address, which is
    // the whole reason the socket error is dug out.
    [Fact]
    public void Describe_ConnectionRefused_SaysNothingIsListening()
    {
        HttpFailureSummary.Describe(WithSocket(SocketError.ConnectionRefused), Host)
            .Should().Be($"{Host} refused the connection. It is reachable, but nothing is listening on that port.");
    }

    [Theory]
    [InlineData(SocketError.HostNotFound)]
    [InlineData(SocketError.NoData)]
    public void Describe_UnresolvableHost_SaysSo(SocketError error)
    {
        HttpFailureSummary.Describe(WithSocket(error), Host).Should().Be($"{Host} could not be resolved.");
    }

    [Fact]
    public void Describe_SocketTimeout_ReadsAsTimeout()
    {
        HttpFailureSummary.Describe(WithSocket(SocketError.TimedOut), Host)
            .Should().Be($"{Host} did not answer in time. Check connectivity or firewall rules.");
    }

    [Fact]
    public void Describe_UnrecognizedSocketError_FallsBackToUnreachable()
    {
        HttpFailureSummary.Describe(WithSocket(SocketError.NetworkDown), Host)
            .Should().Be($"Could not reach {Host}.");
    }

    // HttpClient wraps the socket error one level deeper on some paths, so both depths resolve.
    [Fact]
    public void Describe_NestedSocketError_IsStillFound()
    {
        var nested = new HttpRequestException(
            "outer",
            new IOException("inner", new SocketException((int)SocketError.ConnectionRefused)));

        HttpFailureSummary.Describe(nested, Host)
            .Should().Be($"{Host} refused the connection. It is reachable, but nothing is listening on that port.");
    }

    [Fact]
    public void Describe_TransportFailureWithNoSocketError_FallsBackToUnreachable()
    {
        HttpFailureSummary.Describe(new HttpRequestException("no inner"), Host)
            .Should().Be($"Could not reach {Host}.");
    }

    // Anything that is not a transport failure at all (a parse error, say) keeps its own text,
    // because there is nothing more specific to say about it.
    [Fact]
    public void Describe_NonHttpException_KeepsItsMessage()
    {
        HttpFailureSummary.Describe(new InvalidOperationException("stats payload was not JSON"), Host)
            .Should().Be($"Could not read from {Host}: stats payload was not JSON");
    }

    [Fact]
    public void Describe_NullException_DoesNotThrow()
    {
        HttpFailureSummary.Describe(null, Host).Should().Be($"Could not read from {Host}: ");
    }

    // This renders inline on a card, so a multi-line or runaway exception cannot be passed through
    // whole.
    [Fact]
    public void Describe_MultiLineMessage_KeepsOnlyTheFirstLine()
    {
        var ex = new InvalidOperationException("first line\r\nsecond line\r\nthird line");

        HttpFailureSummary.Describe(ex, Host).Should().Be($"Could not read from {Host}: first line");
    }

    [Fact]
    public void Describe_LongMessage_IsCapped()
    {
        var ex = new InvalidOperationException(new string('x', 500));

        var result = HttpFailureSummary.Describe(ex, Host);

        result.Should().Be($"Could not read from {Host}: {new string('x', 160)}...");
    }

    [Fact]
    public void Describe_MessageAtTheCap_IsNotTruncated()
    {
        var ex = new InvalidOperationException(new string('x', 160));

        HttpFailureSummary.Describe(ex, Host).Should().Be($"Could not read from {Host}: {new string('x', 160)}");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Describe_WithoutAHost_NamesTheDeviceGenerically(string host)
    {
        HttpFailureSummary.Describe(new HttpRequestException("no inner"), host)
            .Should().Be("Could not reach the device.");
    }

    [Fact]
    public void ForResponse_WithoutAStatus_BlamesTheBody()
    {
        HttpFailureSummary.ForResponse(null, Host)
            .Should().Be($"{Host} answered, but not with stats this optimizer can read.");
    }

    // A response that arrived carries the same status wording as a thrown one, so the reader is
    // not told two different things about the same 401.
    [Fact]
    public void ForResponse_WithAStatus_MatchesDescribe()
    {
        HttpFailureSummary.ForResponse(HttpStatusCode.Unauthorized, Host)
            .Should().Be(HttpFailureSummary.Describe(WithStatus(HttpStatusCode.Unauthorized), Host));
    }

    [Fact]
    public void ForResponse_WithoutAHost_NamesTheDeviceGenerically()
    {
        HttpFailureSummary.ForResponse(null, "")
            .Should().Be("the device answered, but not with stats this optimizer can read.");
    }
}
