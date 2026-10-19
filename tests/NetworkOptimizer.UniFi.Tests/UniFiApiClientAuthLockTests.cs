using System.Net;
using System.Net.Sockets;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace NetworkOptimizer.UniFi.Tests;

/// <summary>
/// A login queued behind another login must never wait forever. A reconnect disposes the old
/// client while calls on it are still queued for the login lock.
/// </summary>
public sealed class UniFiApiClientAuthLockTests : IDisposable
{
    // Accepts connections and never answers, so a login against it holds the lock until its own
    // request timeouts fire (several seconds).
    private readonly TcpListener _silent = new(IPAddress.Loopback, 0);

    public UniFiApiClientAuthLockTests() => _silent.Start();

    public void Dispose() => _silent.Stop();

    private UniFiApiClient NewClient() =>
        new(NullLogger<UniFiApiClient>.Instance, $"127.0.0.1:{((IPEndPoint)_silent.LocalEndpoint).Port}", "User1", "pw");

    // Disposed is reported the way a login on a disposed client always was, so the existing
    // ObjectDisposedException handling keeps treating it as a skip.
    [Fact]
    public async Task AQueuedLogin_EndsAsDisposed_WhenTheClientIsDisposed()
    {
        var client = NewClient();
        var holder = client.LoginAsync();
        await Task.Delay(300);
        var waiter = client.LoginAsync();
        await Task.Delay(300);
        waiter.IsCompleted.Should().BeFalse("the first login still holds the lock");

        client.Dispose();

        var act = () => waiter.WaitAsync(TimeSpan.FromSeconds(3));
        await act.Should().ThrowAsync<ObjectDisposedException>();
        (await holder.WaitAsync(TimeSpan.FromSeconds(15))).Should().BeFalse();
    }

    [Fact]
    public async Task ALoginOnADisposedClient_EndsAsDisposed()
    {
        var client = NewClient();
        client.Dispose();

        var act = () => client.LoginAsync().WaitAsync(TimeSpan.FromSeconds(3));
        await act.Should().ThrowAsync<ObjectDisposedException>();
    }

    [Fact]
    public async Task AQueuedLogin_ReturnsFalse_AfterTheLockWait()
    {
        var client = NewClient();
        client.AuthLockWait = TimeSpan.FromMilliseconds(300);
        var holder = client.LoginAsync();
        await Task.Delay(300);

        var waiter = client.LoginAsync();

        (await waiter.WaitAsync(TimeSpan.FromSeconds(3))).Should().BeFalse();
        holder.IsCompleted.Should().BeFalse("the first login is still in flight");
        client.Dispose();
        await holder.WaitAsync(TimeSpan.FromSeconds(15));
    }

    [Fact]
    public async Task AQueuedLogin_StillThrows_WhenItsCallerCancels()
    {
        var client = NewClient();
        var holder = client.LoginAsync();
        await Task.Delay(300);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));

        var act = () => client.LoginAsync(cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        client.Dispose();
        await holder.WaitAsync(TimeSpan.FromSeconds(15));
    }
}
