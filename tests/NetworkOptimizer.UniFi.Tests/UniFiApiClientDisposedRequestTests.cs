using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace NetworkOptimizer.UniFi.Tests;

/// <summary>
/// Regression coverage for the disposed-client request guard: a concurrent
/// reconnect can dispose a <see cref="UniFiApiClient"/> while a data call is in
/// flight, so <c>ExecuteRequestAsync</c> must turn an
/// <see cref="ObjectDisposedException"/> (and a disposed client) into the same
/// "couldn't complete" value these methods already yield on failure, rather than
/// letting the exception surface as a crash.
/// </summary>
public class UniFiApiClientDisposedRequestTests
{
    private static UniFiApiClient CreateClient() =>
        new(NullLogger<UniFiApiClient>.Instance, "unifi.example.test", "user", "pass");

    [Fact]
    public async Task ExecuteRequestAsync_returns_action_result_on_happy_path()
    {
        using var client = CreateClient();

        var result = await client.ExecuteRequestAsync(async () =>
        {
            await Task.Yield();
            return 42;
        });

        // Happy path is untouched: the wrapper returns exactly what the action produced.
        result.Should().Be(42);
    }

    [Fact]
    public async Task ExecuteRequestAsync_swallows_ObjectDisposedException_and_returns_default()
    {
        using var client = CreateClient();

        // Mid-flight disposal: the in-flight request throws ObjectDisposedException
        // (the retry policy does not handle it, so it would otherwise propagate).
        var result = await client.ExecuteRequestAsync<string?>(() =>
            throw new ObjectDisposedException(nameof(UniFiApiClient)));

        result.Should().BeNull();
    }

    [Fact]
    public async Task ExecuteRequestAsync_short_circuits_after_dispose_without_invoking_action()
    {
        var client = CreateClient();
        client.Dispose();

        var actionInvoked = false;
        var result = await client.ExecuteRequestAsync(async () =>
        {
            actionInvoked = true;
            await Task.Yield();
            return 42;
        });

        // Disposed fast-path: the action never runs and the wrapper returns default.
        actionInvoked.Should().BeFalse();
        result.Should().Be(0);
    }
}
