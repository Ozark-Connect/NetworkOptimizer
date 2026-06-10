using System.Text.Json;
using FluentAssertions;
using Xunit;

namespace NetworkOptimizer.UniFi.Tests;

/// <summary>
/// Tests for the non-JSON response wrapper thrown when the UniFi Console serves
/// HTML (login/error page during reboot or firmware upgrade) instead of JSON.
/// The wrapper must stay catch-compatible with JsonException so existing
/// handlers and retry predicates behave exactly as before.
/// </summary>
public class UniFiNonJsonResponseExceptionTests
{
    private static readonly JsonException Inner = new("'<' is an invalid start of a value.");

    [Fact]
    public void Create_IsCatchableAsJsonException()
    {
        var ex = UniFiNonJsonResponseException.Create(200, "text/html", "<!DOCTYPE html>", Inner);

        ex.Should().BeAssignableTo<JsonException>();
        ex.InnerException.Should().BeSameAs(Inner);
    }

    [Fact]
    public void Create_MessageContainsStatusContentTypeAndPreview()
    {
        var ex = UniFiNonJsonResponseException.Create(503, "text/html", "<!DOCTYPE html><html>", Inner);

        ex.Message.Should().Contain("status 503");
        ex.Message.Should().Contain("content-type text/html");
        ex.Message.Should().Contain("<!DOCTYPE html><html>");
    }

    [Fact]
    public void Create_TruncatesLongBodies()
    {
        var body = new string('x', 500);

        var ex = UniFiNonJsonResponseException.Create(200, "text/html", body, Inner);

        ex.Message.Should().Contain(new string('x', 120) + "...");
        ex.Message.Should().NotContain(new string('x', 121));
    }

    [Fact]
    public void Create_FlattensNewlinesInPreview()
    {
        var ex = UniFiNonJsonResponseException.Create(200, "text/html", "<html>\r\n<body>\nhi", Inner);

        ex.Message.Should().NotContain("\n");
        ex.Message.Should().NotContain("\r");
    }

    [Fact]
    public void Create_HandlesNullBodyAndContentType()
    {
        var ex = UniFiNonJsonResponseException.Create(502, null, null, Inner);

        ex.Message.Should().Contain("status 502");
        ex.Message.Should().Contain("content-type unknown");
    }
}
