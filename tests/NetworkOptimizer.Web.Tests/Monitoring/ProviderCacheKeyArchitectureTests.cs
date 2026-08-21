using System.Collections;
using System.Collections.Concurrent;
using System.Reflection;
using FluentAssertions;
using NetworkOptimizer.Monitoring.Providers;
using NetworkOptimizer.Web.Services.CableModemProviders;
using Xunit;

namespace NetworkOptimizer.Web.Tests.Monitoring;

/// <summary>
/// Poll providers are registered as singletons and shared by every site, while
/// each site's database numbers its own configurations from 1. A per-device
/// cache keyed on the configuration ID therefore collides across sites: the
/// first modem, ONT or dish added at any site is Id 1.
///
/// This net catches a cache keyed on an integer. It cannot catch one keyed on
/// the wrong string - reviewing a new dictionary key is still a reading job.
/// </summary>
public class ProviderCacheKeyArchitectureTests
{
    private static readonly Assembly WebAssembly = typeof(XfinityGatewayProvider).Assembly;

    private static readonly Type[] ProviderInterfaces =
    {
        typeof(ICableModemProvider),
        typeof(ICellularModemProvider),
        typeof(IOntProvider),
        typeof(IStarlinkProvider),
    };

    private static IReadOnlyList<Type> ProviderTypes() => WebAssembly.GetTypes()
        .Where(t => t is { IsClass: true, IsAbstract: false })
        .Where(t => ProviderInterfaces.Any(i => i.IsAssignableFrom(t)))
        .OrderBy(t => t.FullName)
        .ToList();

    public static TheoryData<Type> Providers()
    {
        var data = new TheoryData<Type>();
        foreach (var type in ProviderTypes())
            data.Add(type);

        return data;
    }

    [Fact]
    public void TheNetSeesEveryProvider()
    {
        // A silent zero here would make every assertion below vacuous.
        ProviderTypes().Should().HaveCountGreaterThan(10);
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public void ProviderCachesAreNotKeyedOnAConfigurationId(Type providerType)
    {
        var offenders = providerType
            .GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
            .Where(f => IsKeyedCollection(f.FieldType))
            .Where(f => KeyType(f.FieldType) == typeof(int))
            .Select(f => $"{providerType.Name}.{f.Name}")
            .ToList();

        offenders.Should().BeEmpty(
            "a provider singleton is shared by every site, so an int-keyed per-device cache " +
            "collides whenever two sites both have a device with that configuration ID - " +
            "key on the poll context's CacheKey instead");
    }

    private static bool IsKeyedCollection(Type type)
    {
        if (!type.IsGenericType)
            return false;

        return typeof(IDictionary).IsAssignableFrom(type)
            || type.GetGenericTypeDefinition() == typeof(ConcurrentDictionary<,>);
    }

    private static Type? KeyType(Type type) =>
        type.IsGenericType && type.GetGenericArguments().Length == 2
            ? type.GetGenericArguments()[0]
            : null;
}
