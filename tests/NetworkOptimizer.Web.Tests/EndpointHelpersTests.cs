using System.Net;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using NetworkOptimizer.Web.Endpoints;
using Xunit;

namespace NetworkOptimizer.Web.Tests;

/// <summary>
/// Verifies the client-IP capture entry point normalizes IPv4-mapped IPv6 addresses. This is the
/// exact path an IPv4 client hits when Kestrel accepts it on a dual-stack socket (the Docker/
/// Proxmox default) and its address surfaces as ::ffff:a.b.c.d - reproduced here with a synthetic
/// <see cref="DefaultHttpContext"/> so no dual-stack environment is needed.
/// </summary>
public class EndpointHelpersTests
{
    private static HttpContext ContextWith(IPAddress? remote, string? forwardedFor = null)
    {
        var ctx = new DefaultHttpContext();
        ctx.Connection.RemoteIpAddress = remote;
        if (forwardedFor != null)
            ctx.Request.Headers["X-Forwarded-For"] = forwardedFor;
        return ctx;
    }

    [Fact]
    public void GetClientIp_UnwrapsMappedRemoteAddress()
    {
        var ctx = ContextWith(IPAddress.Parse("::ffff:10.20.30.40"));
        EndpointHelpers.GetClientIp(ctx).Should().Be("10.20.30.40");
    }

    [Fact]
    public void GetClientIp_PlainIPv4RemoteAddress_Unchanged()
    {
        var ctx = ContextWith(IPAddress.Parse("10.20.30.40"));
        EndpointHelpers.GetClientIp(ctx).Should().Be("10.20.30.40");
    }

    [Fact]
    public void GetClientIp_RealIPv6RemoteAddress_LeftUntouched()
    {
        // Documentation IPv6 (RFC 3849) - a genuine IPv6 client must keep its identity.
        var ctx = ContextWith(IPAddress.Parse("2001:db8::1"));
        EndpointHelpers.GetClientIp(ctx).Should().Be("2001:db8::1");
    }

    [Fact]
    public void GetClientIp_MappedForwardedFor_TakesPrecedenceAndUnwraps()
    {
        // XFF wins over the connection address, and it too gets normalized.
        // 198.51.100.0/24 is RFC 5737 TEST-NET-2.
        var ctx = ContextWith(IPAddress.Loopback, forwardedFor: "::ffff:198.51.100.7");
        EndpointHelpers.GetClientIp(ctx).Should().Be("198.51.100.7");
    }

    [Fact]
    public void GetClientIp_NoRemoteAddress_ReturnsUnknown()
    {
        var ctx = ContextWith(remote: null);
        EndpointHelpers.GetClientIp(ctx).Should().Be("unknown");
    }
}
