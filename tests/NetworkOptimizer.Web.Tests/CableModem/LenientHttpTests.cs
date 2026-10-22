using System.Text;
using FluentAssertions;
using NetworkOptimizer.Web.Services.CableModemProviders;
using Xunit;

namespace NetworkOptimizer.Web.Tests.CableModem;

public class LenientHttpTests
{
    private static byte[] Raw(string text) => Encoding.ASCII.GetBytes(text.Replace("\n", "\r\n"));

    [Fact]
    public void ParseRawHttpResponse_ParsesWellFormedResponse()
    {
        var raw = Raw(
            "HTTP/1.1 200 OK\n" +
            "Content-Type: text/html\n" +
            "Content-Length: 5\n" +
            "\n" +
            "hello");

        var resp = LenientHttp.ParseRawHttpResponse(raw, raw.Length);

        resp.StatusCode.Should().Be(200);
        resp.Headers["Content-Type"].Should().Be("text/html");
        resp.Body.Should().Be("hello");
    }

    // The CM700's NET-DK/1.0 server intermittently drops the leading bytes of a header line. The
    // HAR for issue #869 shows "Content-Length" arriving as "ntent-Length" (the "Co" dropped).
    // That line still has a colon, so the lenient reader keeps it (as an unrecognized header) and,
    // with no usable Content-Length, returns the whole body that arrived before the connection
    // closed. .NET's strict parser would not choke on this colon-bearing line, but the reader must
    // still produce the right body without a real Content-Length.
    [Fact]
    public void ParseRawHttpResponse_ToleratesDroppedHeaderNameBytes()
    {
        var raw = Raw(
            "HTTP/1.1 200 OK\n" +
            "Content-Type: text/html\n" +
            "ntent-Length: 50471\n" +
            "Connection: close\n" +
            "\n" +
            "<html>dsTable</html>");

        var resp = LenientHttp.ParseRawHttpResponse(raw, raw.Length);

        resp.StatusCode.Should().Be(200);
        resp.Headers.Should().NotContainKey("Content-Length");
        // No usable Content-Length, so the body is everything after the header separator.
        resp.Body.Should().Be("<html>dsTable</html>");
    }

    // The reporter's log showed `Received an invalid header line: 'ceived response error'.` - the
    // same byte-drop landing on a line that lost its colon entirely ("Received response error"
    // minus "Re"). .NET aborts the whole response on such a line; the lenient reader skips it and
    // still returns the status and body.
    [Fact]
    public void ParseRawHttpResponse_SkipsColonlessCorruptedLine()
    {
        var raw = Raw(
            "HTTP/1.1 200 OK\n" +
            "Content-Type: text/html\n" +
            "ceived response error\n" +
            "Connection: close\n" +
            "\n" +
            "<html>dsTable</html>");

        var resp = LenientHttp.ParseRawHttpResponse(raw, raw.Length);

        resp.StatusCode.Should().Be(200);
        resp.Headers["Content-Type"].Should().Be("text/html");
        resp.Body.Should().Be("<html>dsTable</html>");
    }

    [Fact]
    public void ParseRawHttpResponse_RecoversStatusFromClippedStatusLine()
    {
        // Leading bytes dropped from the status line too: "HTTP/1.1 401 Unauthorized" -> the code
        // is still recovered from the first 3-digit token.
        var raw = Raw(
            "TP/1.1 401 Unauthorized\n" +
            "WWW-Authenticate: Basic realm=\"NETGEAR CM700\"\n" +
            "Set-Cookie: XSRF_TOKEN=2104060059; Path=/\n" +
            "\n");

        var resp = LenientHttp.ParseRawHttpResponse(raw, raw.Length);

        resp.StatusCode.Should().Be(401);
        resp.Headers["Set-Cookie"].Should().Contain("XSRF_TOKEN=2104060059");
    }

    [Fact]
    public void ParseRawHttpResponse_HandlesBareLfLineEndings()
    {
        var raw = Encoding.ASCII.GetBytes(
            "HTTP/1.1 200 OK\n" +
            "Content-Type: text/html\n" +
            "\n" +
            "body-bytes");

        var resp = LenientHttp.ParseRawHttpResponse(raw, raw.Length);

        resp.StatusCode.Should().Be(200);
        resp.Headers["Content-Type"].Should().Be("text/html");
        resp.Body.Should().Be("body-bytes");
    }

    [Fact]
    public void ParseRawHttpResponse_BoundsBodyByContentLengthWhenValid()
    {
        var raw = Raw(
            "HTTP/1.1 200 OK\n" +
            "Content-Length: 5\n" +
            "\n" +
            "helloEXTRA");

        var resp = LenientHttp.ParseRawHttpResponse(raw, raw.Length);

        resp.Body.Should().Be("hello");
    }

    // The ARRIS SB6141's exact header set: no Content-Length, body ended by closing the connection.
    [Fact]
    public void ParseRawHttpResponse_ReadsCloseDelimitedSb6141Response()
    {
        var raw = Raw(
            "HTTP/1.1 200 Ok\n" +
            "Pragma: no-cache\n" +
            "Content-Type: text/html\n" +
            "Date: Tue, 20 Oct 2026 12:02:46 GMT\n" +
            "X-Frame-Options: SAMEORIGIN\n" +
            "Expires: -1\n" +
            "Connection: close\n" +
            "Server: NET-DK/1.0\n" +
            "\n" +
            "<TABLE><TR><TH>Signal Stats (Codewords)</TH></TR></TABLE>");

        var resp = LenientHttp.ParseRawHttpResponse(raw, raw.Length);

        resp.StatusCode.Should().Be(200);
        resp.Headers["Server"].Should().Be("NET-DK/1.0");
        resp.Headers.Should().NotContainKey("Content-Length");
        resp.Body.Should().Be("<TABLE><TR><TH>Signal Stats (Codewords)</TH></TR></TABLE>");
    }
}
