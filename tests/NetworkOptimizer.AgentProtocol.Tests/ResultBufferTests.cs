using FluentAssertions;
using Xunit;

namespace NetworkOptimizer.AgentProtocol.Tests;

public class ResultBufferTests
{
    private static AgentMessage ProbeMessage(string targetId, long timestampMs = 1000) => new()
    {
        ProbeResults = new ProbeResultBatch
        {
            Results =
            {
                new ProbeResult
                {
                    TargetId = targetId,
                    TimestampUnixMs = timestampMs,
                    Success = true,
                    Sent = 5,
                    Received = 5,
                }
            }
        }
    };

    private static AgentMessage SnmpMessage(string deviceMac) => new()
    {
        SnmpResults = new SnmpResultBatch
        {
            Interfaces =
            {
                new SnmpInterfaceSample
                {
                    DeviceMac = deviceMac,
                    IfName = "eth0",
                    InOctets = 123456,
                    OutOctets = 654321,
                    TimestampUnixMs = 1000,
                }
            }
        }
    };

    [Fact]
    public async Task DequeuesInFifoOrder()
    {
        var buffer = new ResultBuffer();
        buffer.Enqueue(ProbeMessage("a"));
        buffer.Enqueue(SnmpMessage("aa:bb:cc:dd:ee:ff"));
        buffer.Enqueue(ProbeMessage("b"));

        (await buffer.DequeueAsync(CancellationToken.None)).ProbeResults.Results[0].TargetId.Should().Be("a");
        (await buffer.DequeueAsync(CancellationToken.None)).PayloadCase
            .Should().Be(AgentMessage.PayloadOneofCase.SnmpResults);
        (await buffer.DequeueAsync(CancellationToken.None)).ProbeResults.Results[0].TargetId.Should().Be("b");
        buffer.Count.Should().Be(0);
    }

    [Fact]
    public async Task DequeueWaitsForEnqueue()
    {
        var buffer = new ResultBuffer();
        var dequeue = buffer.DequeueAsync(CancellationToken.None).AsTask();
        dequeue.IsCompleted.Should().BeFalse();

        buffer.Enqueue(ProbeMessage("late"));
        var message = await dequeue.WaitAsync(TimeSpan.FromSeconds(5));
        message.ProbeResults.Results[0].TargetId.Should().Be("late");
    }

    [Fact]
    public async Task DequeueHonorsCancellation()
    {
        var buffer = new ResultBuffer();
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        var act = async () => await buffer.DequeueAsync(cts.Token);
        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public void ByteCapDropsOldestFirst()
    {
        var single = ProbeMessage("x").CalculateSize();
        // Room for roughly three single-result messages.
        var buffer = new ResultBuffer(maxBytes: single * 3 + 1);

        for (var i = 0; i < 10; i++)
            buffer.Enqueue(ProbeMessage($"t{i}"));

        buffer.Count.Should().BeLessThan(10);
        buffer.DroppedTotal.Should().Be(10 - buffer.Count);
        buffer.ApproxBytes.Should().BeLessThanOrEqualTo(single * 3 + 1);

        // The survivors are the newest, still in FIFO order.
        var survivors = new List<int>();
        while (buffer.TryDequeueIf(_ => true, out var message))
            survivors.Add(int.Parse(message.ProbeResults.Results[0].TargetId[1..]));
        survivors.Should().BeInAscendingOrder();
        survivors.Last().Should().Be(9);
    }

    [Fact]
    public async Task AgeCapDropsExpiredEntries()
    {
        var buffer = new ResultBuffer(maxAge: TimeSpan.FromMilliseconds(50));
        buffer.Enqueue(ProbeMessage("old"));
        await Task.Delay(150);
        buffer.Enqueue(ProbeMessage("new"));

        buffer.Count.Should().Be(1);
        buffer.DroppedTotal.Should().Be(1);
        (await buffer.DequeueAsync(CancellationToken.None)).ProbeResults.Results[0].TargetId.Should().Be("new");
    }

    [Fact]
    public async Task EvictedPermitsDoNotStrandTheConsumer()
    {
        // Eviction removes entries without consuming semaphore permits; a
        // consumer waking to an empty buffer must keep waiting, then still
        // receive the next real message.
        var buffer = new ResultBuffer(maxAge: TimeSpan.FromMilliseconds(50));
        buffer.Enqueue(ProbeMessage("doomed"));
        await Task.Delay(150);
        buffer.Enqueue(ProbeMessage("evictor")); // evicts "doomed" on enqueue

        (await buffer.DequeueAsync(CancellationToken.None)).ProbeResults.Results[0].TargetId.Should().Be("evictor");

        var pending = buffer.DequeueAsync(CancellationToken.None).AsTask();
        buffer.Enqueue(ProbeMessage("after"));
        (await pending.WaitAsync(TimeSpan.FromSeconds(5))).ProbeResults.Results[0].TargetId.Should().Be("after");
    }

    [Fact]
    public async Task RequeueFrontRestoresOrderAheadOfNewerEntries()
    {
        var buffer = new ResultBuffer();
        buffer.Enqueue(ProbeMessage("newer"));

        buffer.RequeueFront([ProbeMessage("salvaged-1"), ProbeMessage("salvaged-2")]);

        (await buffer.DequeueAsync(CancellationToken.None)).ProbeResults.Results[0].TargetId.Should().Be("salvaged-1");
        (await buffer.DequeueAsync(CancellationToken.None)).ProbeResults.Results[0].TargetId.Should().Be("salvaged-2");
        (await buffer.DequeueAsync(CancellationToken.None)).ProbeResults.Results[0].TargetId.Should().Be("newer");
    }

    [Fact]
    public void RequeueFrontOfNothingIsANoOp()
    {
        var buffer = new ResultBuffer();
        buffer.RequeueFront([]);
        buffer.Count.Should().Be(0);
        buffer.ApproxBytes.Should().Be(0);
    }

    [Fact]
    public void TryDequeueIfOnlyTakesMatchingHead()
    {
        var buffer = new ResultBuffer();
        buffer.TryDequeueIf(_ => true, out _).Should().BeFalse("buffer is empty");

        buffer.Enqueue(SnmpMessage("aa:bb:cc:dd:ee:ff"));
        buffer.Enqueue(ProbeMessage("behind"));

        buffer.TryDequeueIf(m => m.PayloadCase == AgentMessage.PayloadOneofCase.ProbeResults, out _)
            .Should().BeFalse("head is an SNMP batch");
        buffer.TryDequeueIf(m => m.PayloadCase == AgentMessage.PayloadOneofCase.SnmpResults, out var snmp)
            .Should().BeTrue();
        snmp.SnmpResults.Interfaces[0].DeviceMac.Should().Be("aa:bb:cc:dd:ee:ff");
        buffer.Count.Should().Be(1);
    }

    [Fact]
    public void TakeDroppedCountResetsBetweenCalls()
    {
        var single = ProbeMessage("x").CalculateSize();
        var buffer = new ResultBuffer(maxBytes: single);
        buffer.Enqueue(ProbeMessage("a"));
        buffer.Enqueue(ProbeMessage("b")); // drops "a"

        buffer.TakeDroppedCount().Should().Be(1);
        buffer.TakeDroppedCount().Should().Be(0);
        buffer.DroppedTotal.Should().Be(1);
    }

    [Fact]
    public void ByteAccountingTracksEnqueueAndDequeue()
    {
        var buffer = new ResultBuffer();
        var message = ProbeMessage("sized");
        buffer.Enqueue(message);
        buffer.ApproxBytes.Should().Be(message.CalculateSize());

        buffer.TryDequeueIf(_ => true, out _).Should().BeTrue();
        buffer.ApproxBytes.Should().Be(0);
    }

    [Fact]
    public async Task ConcurrentProducersAndConsumerDeliverEverything()
    {
        var buffer = new ResultBuffer();
        const int perProducer = 200;
        var producers = Enumerable.Range(0, 4).Select(p => Task.Run(() =>
        {
            for (var i = 0; i < perProducer; i++)
                buffer.Enqueue(ProbeMessage($"p{p}-{i}"));
        })).ToArray();

        var seen = new List<string>();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        while (seen.Count < 4 * perProducer)
        {
            var message = await buffer.DequeueAsync(cts.Token);
            seen.Add(message.ProbeResults.Results[0].TargetId);
        }

        await Task.WhenAll(producers);
        seen.Should().HaveCount(4 * perProducer);
        seen.Should().OnlyHaveUniqueItems();

        // Per-producer FIFO order survives interleaving.
        for (var p = 0; p < 4; p++)
        {
            var indices = seen.Where(id => id.StartsWith($"p{p}-"))
                .Select(id => int.Parse(id.Split('-')[1])).ToList();
            indices.Should().BeInAscendingOrder();
        }
    }
}
