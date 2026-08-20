using System.Reflection;
using FluentAssertions;
using NetworkOptimizer.Web.Services;
using Xunit;

namespace NetworkOptimizer.Web.Tests;

/// <summary>
/// Guards the dependency shape of services that create and dispose DI scopes synchronously.
/// </summary>
public class WiFiOptimizerServiceDisposalTests
{
    /// <summary>
    /// <see cref="WiFiOptimizerService"/> creates scopes with IServiceScopeFactory.CreateScope()
    /// and disposes them synchronously. A scope containing a service that implements only
    /// IAsyncDisposable throws on synchronous disposal ("type only implements IAsyncDisposable"),
    /// which does not surface as a DI error - it surfaces as the feature silently failing. Injecting
    /// MonitoringInfluxClient here did exactly that: propagation loading threw on scope disposal and
    /// the whole channel analysis reported "unavailable, ensure APs are online".
    ///
    /// Take the owning registry instead, which hands back an instance the scope does not own.
    /// </summary>
    /// <remarks>
    /// Targets the scoped registration specifically. The registry is also IAsyncDisposable but is a
    /// singleton, so it is disposed by the root container and never by one of these scopes - which
    /// is exactly why it is the safe dependency. Reflection cannot see DI lifetimes, so this names
    /// the offending type rather than inferring the rule.
    /// </remarks>
    [Fact]
    public void Constructor_DoesNotTakeTheScopedInfluxClient()
    {
        var ctor = typeof(WiFiOptimizerService).GetConstructors(
            BindingFlags.Public | BindingFlags.Instance).Single();

        ctor.GetParameters().Select(p => p.ParameterType)
            .Should().NotContain(typeof(NetworkOptimizer.Storage.Services.MonitoringInfluxClient),
                "it is registered scoped and implements only IAsyncDisposable, so a synchronously "
                + "disposed scope throws; take MonitoringInfluxRegistry instead");
    }
}
