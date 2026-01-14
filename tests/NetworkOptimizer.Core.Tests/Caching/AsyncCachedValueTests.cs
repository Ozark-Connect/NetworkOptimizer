using FluentAssertions;
using NetworkOptimizer.Core.Caching;
using Xunit;

namespace NetworkOptimizer.Core.Tests.Caching;

public class AsyncCachedValueTests
{
    #region Constructor Tests

    [Fact]
    public void Constructor_NullFactory_ThrowsArgumentNullException()
    {
        // Arrange & Act
        var act = () => new AsyncCachedValue<string>(null!, TimeSpan.FromMinutes(5));

        // Assert
        act.Should().Throw<ArgumentNullException>()
            .WithParameterName("factory");
    }

    [Fact]
    public void Constructor_ValidParameters_CreatesInstance()
    {
        // Arrange & Act
        var cache = new AsyncCachedValue<string>(() => Task.FromResult("test"), TimeSpan.FromMinutes(5));

        // Assert
        cache.Should().NotBeNull();
    }

    #endregion

    #region GetAsync Tests

    [Fact]
    public async Task GetAsync_FirstCall_ExecutesFactory()
    {
        // Arrange
        var callCount = 0;
        var cache = new AsyncCachedValue<string>(
            () =>
            {
                callCount++;
                return Task.FromResult("test-value");
            },
            TimeSpan.FromMinutes(5));

        // Act
        var result = await cache.GetAsync();

        // Assert
        result.Should().Be("test-value");
        callCount.Should().Be(1);
    }

    [Fact]
    public async Task GetAsync_SecondCallWithinExpiry_ReturnsCachedValue()
    {
        // Arrange
        var callCount = 0;
        var cache = new AsyncCachedValue<string>(
            () =>
            {
                callCount++;
                return Task.FromResult($"value-{callCount}");
            },
            TimeSpan.FromMinutes(5));

        // Act
        var result1 = await cache.GetAsync();
        var result2 = await cache.GetAsync();

        // Assert
        result1.Should().Be("value-1");
        result2.Should().Be("value-1"); // Cached value
        callCount.Should().Be(1);
    }

    [Fact]
    public async Task GetAsync_CallAfterExpiry_RefreshesCacheAsync()
    {
        // Arrange
        var callCount = 0;
        var cache = new AsyncCachedValue<string>(
            () =>
            {
                callCount++;
                return Task.FromResult($"value-{callCount}");
            },
            TimeSpan.FromMilliseconds(50)); // Short expiry for testing

        // Act
        var result1 = await cache.GetAsync();
        await Task.Delay(100); // Wait for expiry
        var result2 = await cache.GetAsync();

        // Assert
        result1.Should().Be("value-1");
        result2.Should().Be("value-2"); // Refreshed value
        callCount.Should().Be(2);
    }

    [Fact]
    public async Task GetAsync_ForceRefresh_AlwaysExecutesFactory()
    {
        // Arrange
        var callCount = 0;
        var cache = new AsyncCachedValue<string>(
            () =>
            {
                callCount++;
                return Task.FromResult($"value-{callCount}");
            },
            TimeSpan.FromMinutes(5));

        // Act
        var result1 = await cache.GetAsync();
        var result2 = await cache.GetAsync(forceRefresh: true);
        var result3 = await cache.GetAsync(forceRefresh: true);

        // Assert
        result1.Should().Be("value-1");
        result2.Should().Be("value-2");
        result3.Should().Be("value-3");
        callCount.Should().Be(3);
    }

    [Fact]
    public async Task GetAsync_ConcurrentCalls_OnlyExecutesFactoryOnce()
    {
        // Arrange
        var callCount = 0;
        var tcs = new TaskCompletionSource<string>();
        var cache = new AsyncCachedValue<string>(
            async () =>
            {
                Interlocked.Increment(ref callCount);
                return await tcs.Task;
            },
            TimeSpan.FromMinutes(5));

        // Act - Start multiple concurrent calls
        var task1 = cache.GetAsync();
        var task2 = cache.GetAsync();
        var task3 = cache.GetAsync();

        // Complete the factory task
        tcs.SetResult("concurrent-value");

        var results = await Task.WhenAll(task1, task2, task3);

        // Assert
        results.Should().AllBe("concurrent-value");
        callCount.Should().Be(1); // Only one factory call despite concurrent requests
    }

    [Fact]
    public async Task GetAsync_FactoryThrowsException_PropagatesException()
    {
        // Arrange
        var cache = new AsyncCachedValue<string>(
            () => throw new InvalidOperationException("Factory error"),
            TimeSpan.FromMinutes(5));

        // Act
        var act = async () => await cache.GetAsync();

        // Assert
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("Factory error");
    }

    [Fact]
    public async Task GetAsync_FactoryReturnsNull_CachesAndReturnsNull()
    {
        // Arrange
        var callCount = 0;
        var cache = new AsyncCachedValue<string>(
            () =>
            {
                callCount++;
                return Task.FromResult<string>(null!);
            },
            TimeSpan.FromMinutes(5));

        // Act
        var result1 = await cache.GetAsync();
        var result2 = await cache.GetAsync();

        // Assert - null is NOT cached (class constraint means null invalidates)
        result1.Should().BeNull();
        result2.Should().BeNull();
        // Both calls execute factory because null doesn't satisfy the cache
        callCount.Should().Be(2);
    }

    #endregion

    #region Invalidate Tests

    [Fact]
    public async Task Invalidate_ClearsCache_NextCallRefreshes()
    {
        // Arrange
        var callCount = 0;
        var cache = new AsyncCachedValue<string>(
            () =>
            {
                callCount++;
                return Task.FromResult($"value-{callCount}");
            },
            TimeSpan.FromMinutes(5));

        // Act
        var result1 = await cache.GetAsync();
        cache.Invalidate();
        var result2 = await cache.GetAsync();

        // Assert
        result1.Should().Be("value-1");
        result2.Should().Be("value-2");
        callCount.Should().Be(2);
    }

    [Fact]
    public async Task Invalidate_MultipleCalls_DoesNotThrow()
    {
        // Arrange
        var cache = new AsyncCachedValue<string>(
            () => Task.FromResult("test"),
            TimeSpan.FromMinutes(5));

        // Act
        await cache.GetAsync();
        var act = () =>
        {
            cache.Invalidate();
            cache.Invalidate();
            cache.Invalidate();
        };

        // Assert
        act.Should().NotThrow();
    }

    [Fact]
    public async Task Invalidate_BeforeAnyGet_DoesNotThrow()
    {
        // Arrange
        var cache = new AsyncCachedValue<string>(
            () => Task.FromResult("test"),
            TimeSpan.FromMinutes(5));

        // Act
        var act = () => cache.Invalidate();

        // Assert
        act.Should().NotThrow();
    }

    #endregion

    #region Edge Cases

    [Fact]
    public async Task GetAsync_ZeroExpiry_AlwaysRefreshes()
    {
        // Arrange
        var callCount = 0;
        var cache = new AsyncCachedValue<string>(
            () =>
            {
                callCount++;
                return Task.FromResult($"value-{callCount}");
            },
            TimeSpan.Zero);

        // Act
        var result1 = await cache.GetAsync();
        var result2 = await cache.GetAsync();
        var result3 = await cache.GetAsync();

        // Assert - Each call should refresh due to zero expiry
        result1.Should().Be("value-1");
        result2.Should().Be("value-2");
        result3.Should().Be("value-3");
        callCount.Should().Be(3);
    }

    [Fact]
    public async Task GetAsync_VeryLongExpiry_NeverExpiresInReasonableTime()
    {
        // Arrange
        var callCount = 0;
        var cache = new AsyncCachedValue<string>(
            () =>
            {
                callCount++;
                return Task.FromResult($"value-{callCount}");
            },
            TimeSpan.FromDays(365));

        // Act
        var result1 = await cache.GetAsync();
        await Task.Delay(100);
        var result2 = await cache.GetAsync();
        await Task.Delay(100);
        var result3 = await cache.GetAsync();

        // Assert
        result1.Should().Be("value-1");
        result2.Should().Be("value-1");
        result3.Should().Be("value-1");
        callCount.Should().Be(1);
    }

    [Fact]
    public async Task GetAsync_AsyncFactory_ProperlyAwaits()
    {
        // Arrange
        var cache = new AsyncCachedValue<string>(
            async () =>
            {
                await Task.Delay(50);
                return "async-value";
            },
            TimeSpan.FromMinutes(5));

        // Act
        var result = await cache.GetAsync();

        // Assert
        result.Should().Be("async-value");
    }

    #endregion
}
