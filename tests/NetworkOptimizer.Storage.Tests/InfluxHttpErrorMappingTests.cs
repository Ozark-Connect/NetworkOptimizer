using FluentAssertions;
using InfluxDB.Client.Core.Exceptions;
using Xunit;

namespace NetworkOptimizer.Storage.Tests;

/// <summary>
/// The streamed raw-CSV reads map non-2xx responses through the InfluxDB client's own
/// HttpException.Create so the existing NotFound / BadRequest / Unauthorized handling keeps its
/// types. That call is made without RestSharp headers, which the client's own transport always
/// supplies - so these pin that the null-header path produces the right exception type rather than
/// throwing something the read path's filter would miss and let escape as a hard failure.
/// </summary>
public class InfluxHttpErrorMappingTests
{
    private const string InfluxErrorBody = "{\"code\":\"unauthorized\",\"message\":\"unauthorized access\"}";

    [Theory]
    [InlineData(401, typeof(UnauthorizedException))]
    [InlineData(403, typeof(ForbiddenException))]
    [InlineData(404, typeof(NotFoundException))]
    [InlineData(400, typeof(BadRequestException))]
    public void Maps_status_to_the_typed_exception_without_headers(int status, Type expected)
    {
        var ex = HttpException.Create(InfluxErrorBody, null, "reason", (System.Net.HttpStatusCode)status);

        ex.Should().BeOfType(expected);
        ex.Status.Should().Be(status);
    }

    [Fact]
    public void Extracts_the_message_from_an_influx_error_body()
    {
        var ex = HttpException.Create(InfluxErrorBody, null, "reason", System.Net.HttpStatusCode.Unauthorized);

        ex.Message.Should().Contain("unauthorized access");
    }

    [Fact]
    public void A_non_json_body_still_produces_an_exception_rather_than_throwing()
    {
        // A proxy or gateway in front of InfluxDB can return HTML; the read path must still surface a
        // typed failure instead of dying inside its own error handling.
        var act = () => HttpException.Create("<html>502 Bad Gateway</html>", null, "reason",
            System.Net.HttpStatusCode.BadGateway);

        act.Should().NotThrow();
    }
}
