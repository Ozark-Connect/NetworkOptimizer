using FluentAssertions;
using Microsoft.AspNetCore.Http;
using NetworkOptimizer.Web.Services;
using Xunit;

namespace NetworkOptimizer.Web.Tests;

public class CanonicalBaseUrlProviderTests
{
    [Theory]
    [InlineData("/api/health")]
    [InlineData("/API/HEALTH")]
    public void ShouldBypass_AllowsLocalHealthChecks(string path)
    {
        var context = new DefaultHttpContext();
        context.Request.Path = path;

        CanonicalBaseUrlProvider.ShouldBypassRedirect(context.Request).Should().BeTrue();
    }

    [Fact]
    public void ShouldBypass_PreservesGrpcTunnelBypass()
    {
        var context = new DefaultHttpContext();
        context.Request.Path = "/networkoptimizer.AgentService/Connect";
        context.Request.ContentType = "application/grpc+proto";

        CanonicalBaseUrlProvider.ShouldBypassRedirect(context.Request).Should().BeTrue();
    }

    [Fact]
    public void ShouldBypass_KeepsBrowserRoutesCanonical()
    {
        var context = new DefaultHttpContext();
        context.Request.Path = "/dashboard";

        CanonicalBaseUrlProvider.ShouldBypassRedirect(context.Request).Should().BeFalse();
    }
}
