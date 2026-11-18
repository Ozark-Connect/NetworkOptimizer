using FluentAssertions;
using Grpc.Core;
using NetworkOptimizer.Agent;
using Xunit;

namespace NetworkOptimizer.AgentProtocol.Tests;

public class OutOfMemoryRestartTests
{
    private static readonly DateTime T0 = new(2026, 11, 17, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void IsOutOfMemory_DirectException()
    {
        OutOfMemoryRestart.IsOutOfMemory(new OutOfMemoryException()).Should().BeTrue();
    }

    [Fact]
    public void IsOutOfMemory_WrappedAsInnerException()
    {
        var ex = new InvalidOperationException("outer", new OutOfMemoryException());
        OutOfMemoryRestart.IsOutOfMemory(ex).Should().BeTrue();
    }

    [Fact]
    public void IsOutOfMemory_InsideAggregate()
    {
        var ex = new AggregateException(new IOException(), new OutOfMemoryException());
        OutOfMemoryRestart.IsOutOfMemory(ex).Should().BeTrue();
    }

    [Fact]
    public void IsOutOfMemory_RpcDebugException()
    {
        // The tunnel's "Error reading next message" carries the OOM as the status's debug exception.
        var ex = new RpcException(new Status(StatusCode.Internal, "Error reading next message.", new OutOfMemoryException()));
        OutOfMemoryRestart.IsOutOfMemory(ex).Should().BeTrue();
    }

    [Fact]
    public void IsOutOfMemory_RpcDetailOnly()
    {
        var ex = new RpcException(new Status(StatusCode.Internal,
            "Error reading next message. OutOfMemoryException: Exception of type 'System.OutOfMemoryException' was thrown."));
        OutOfMemoryRestart.IsOutOfMemory(ex).Should().BeTrue();
    }

    [Fact]
    public void IsOutOfMemory_OtherFailuresAreIgnored()
    {
        OutOfMemoryRestart.IsOutOfMemory(new IOException("read failed")).Should().BeFalse();
        OutOfMemoryRestart.IsOutOfMemory(new RpcException(new Status(StatusCode.Unavailable, "connection refused"))).Should().BeFalse();
        OutOfMemoryRestart.IsOutOfMemory(null).Should().BeFalse();
    }

    [Fact]
    public void Burst_ReachesTheThresholdInsideTheWindow()
    {
        // A stuck heap fails the conntrack pass every 2 s.
        var burst = new OutOfMemoryRestart.Burst(TimeSpan.FromMinutes(2));
        var counts = Enumerable.Range(0, OutOfMemoryRestart.FailuresBeforeRestart)
            .Select(i => burst.Record(T0.AddSeconds(2 * i))).ToList();

        counts.Should().Equal(1, 2, 3, 4, 5);
        counts[^1].Should().Be(OutOfMemoryRestart.FailuresBeforeRestart);
    }

    [Fact]
    public void Burst_SpreadOutFailuresNeverReachTheThreshold()
    {
        // One stray failure every few minutes ages out before the next arrives.
        var burst = new OutOfMemoryRestart.Burst(TimeSpan.FromMinutes(2));
        for (var i = 0; i < 20; i++)
            burst.Record(T0.AddMinutes(3 * i)).Should().Be(1);
    }

    [Fact]
    public void Burst_OldFailuresAgeOut()
    {
        var burst = new OutOfMemoryRestart.Burst(TimeSpan.FromMinutes(2));
        for (var i = 0; i < 4; i++) burst.Record(T0.AddSeconds(i));

        // At 2:02 the failures at 0 s and 1 s are past the window; 2 s sits on its edge and stays.
        burst.Record(T0.AddMinutes(2).AddSeconds(2)).Should().Be(3);
    }
}
