using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NetworkOptimizer.Core.Extensions;
using Xunit;

namespace NetworkOptimizer.Core.Tests.Extensions;

public class ServiceProviderExtensionsTests
{
    private interface ITestService
    {
        string GetValue();
    }

    private class TestService : ITestService
    {
        public string GetValue() => "test-value";
    }

    private class DisposableTestService : ITestService, IDisposable
    {
        public bool IsDisposed { get; private set; }
        public string GetValue() => IsDisposed ? throw new ObjectDisposedException(nameof(DisposableTestService)) : "disposable-value";
        public void Dispose() => IsDisposed = true;
    }

    #region WithScopedService Tests

    [Fact]
    public void WithScopedService_ResolvesServiceAndExecutesAction()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddScoped<ITestService, TestService>();
        var provider = services.BuildServiceProvider();

        // Act
        var result = provider.WithScopedService<ITestService, string>(service => service.GetValue());

        // Assert
        result.Should().Be("test-value");
    }

    [Fact]
    public void WithScopedService_DisposesScope()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddScoped<DisposableTestService>();
        var provider = services.BuildServiceProvider();

        DisposableTestService? capturedService = null;

        // Act
        provider.WithScopedService<DisposableTestService, string>(service =>
        {
            capturedService = service;
            return service.GetValue();
        });

        // Assert
        capturedService.Should().NotBeNull();
        capturedService!.IsDisposed.Should().BeTrue("scope should dispose the service");
    }

    [Fact]
    public void WithScopedService_ServiceNotRegistered_ThrowsException()
    {
        // Arrange
        var services = new ServiceCollection();
        var provider = services.BuildServiceProvider();

        // Act
        var act = () => provider.WithScopedService<ITestService, string>(s => s.GetValue());

        // Assert
        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void WithScopedService_ActionReturnsNull_ReturnsNull()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddScoped<ITestService, TestService>();
        var provider = services.BuildServiceProvider();

        // Act
        var result = provider.WithScopedService<ITestService, string?>(service => null);

        // Assert
        result.Should().BeNull();
    }

    [Fact]
    public void WithScopedService_ActionThrows_PropagatesException()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddScoped<ITestService, TestService>();
        var provider = services.BuildServiceProvider();

        // Act
        var act = () => provider.WithScopedService<ITestService, string>(service =>
            throw new InvalidOperationException("Action error"));

        // Assert
        act.Should().Throw<InvalidOperationException>().WithMessage("Action error");
    }

    [Fact]
    public void WithScopedService_ReturnsDifferentTypes()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddScoped<ITestService, TestService>();
        var provider = services.BuildServiceProvider();

        // Act
        var stringResult = provider.WithScopedService<ITestService, string>(s => s.GetValue());
        var intResult = provider.WithScopedService<ITestService, int>(s => s.GetValue().Length);
        var boolResult = provider.WithScopedService<ITestService, bool>(s => s.GetValue().StartsWith("test"));

        // Assert
        stringResult.Should().Be("test-value");
        intResult.Should().Be(10);
        boolResult.Should().BeTrue();
    }

    #endregion

    #region WithScopedServiceAsync<T> Tests

    [Fact]
    public async Task WithScopedServiceAsync_ResolvesServiceAndExecutesAction()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddScoped<ITestService, TestService>();
        var provider = services.BuildServiceProvider();

        // Act
        var result = await provider.WithScopedServiceAsync<ITestService, string>(
            async service =>
            {
                await Task.Delay(1);
                return service.GetValue();
            });

        // Assert
        result.Should().Be("test-value");
    }

    [Fact]
    public async Task WithScopedServiceAsync_DisposesScope()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddScoped<DisposableTestService>();
        var provider = services.BuildServiceProvider();

        DisposableTestService? capturedService = null;

        // Act
        await provider.WithScopedServiceAsync<DisposableTestService, string>(
            async service =>
            {
                capturedService = service;
                await Task.Delay(1);
                return service.GetValue();
            });

        // Assert
        capturedService.Should().NotBeNull();
        capturedService!.IsDisposed.Should().BeTrue("scope should dispose the service");
    }

    [Fact]
    public async Task WithScopedServiceAsync_ActionThrows_PropagatesException()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddScoped<ITestService, TestService>();
        var provider = services.BuildServiceProvider();

        // Act
        var act = async () => await provider.WithScopedServiceAsync<ITestService, string>(
            async service =>
            {
                await Task.Delay(1);
                throw new InvalidOperationException("Async error");
            });

        // Assert
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("Async error");
    }

    [Fact]
    public async Task WithScopedServiceAsync_SyncAction_WorksCorrectly()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddScoped<ITestService, TestService>();
        var provider = services.BuildServiceProvider();

        // Act - No await in action
        var result = await provider.WithScopedServiceAsync<ITestService, string>(
            service => Task.FromResult(service.GetValue()));

        // Assert
        result.Should().Be("test-value");
    }

    #endregion

    #region WithScopedServiceAsync (void return) Tests

    [Fact]
    public async Task WithScopedServiceAsyncVoid_ResolvesServiceAndExecutesAction()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddScoped<ITestService, TestService>();
        var provider = services.BuildServiceProvider();
        var wasExecuted = false;

        // Act
        await provider.WithScopedServiceAsync<ITestService>(
            async service =>
            {
                await Task.Delay(1);
                wasExecuted = true;
                _ = service.GetValue();
            });

        // Assert
        wasExecuted.Should().BeTrue();
    }

    [Fact]
    public async Task WithScopedServiceAsyncVoid_DisposesScope()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddScoped<DisposableTestService>();
        var provider = services.BuildServiceProvider();

        DisposableTestService? capturedService = null;

        // Act
        await provider.WithScopedServiceAsync<DisposableTestService>(
            async service =>
            {
                capturedService = service;
                await Task.Delay(1);
            });

        // Assert
        capturedService.Should().NotBeNull();
        capturedService!.IsDisposed.Should().BeTrue("scope should dispose the service");
    }

    [Fact]
    public async Task WithScopedServiceAsyncVoid_ActionThrows_PropagatesException()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddScoped<ITestService, TestService>();
        var provider = services.BuildServiceProvider();

        // Act
        var act = async () => await provider.WithScopedServiceAsync<ITestService>(
            async service =>
            {
                await Task.Delay(1);
                throw new InvalidOperationException("Void async error");
            });

        // Assert
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("Void async error");
    }

    #endregion
}
