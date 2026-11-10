using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NetworkOptimizer.Storage.Models;
using NetworkOptimizer.Storage.Services;
using Xunit;

namespace NetworkOptimizer.Storage.Tests;

/// <summary>
/// Golden Flux text for every public query method of <see cref="MonitoringInfluxClient"/>, built from
/// ordinary inputs (MACs, interface names, OID field names, target ids). A golden that changes means a
/// query changed. Set NO_UPDATE_FLUX_GOLDENS=1 to rewrite the files after a deliberate change.
/// </summary>
public sealed class MonitoringInfluxFluxGoldenTests : IDisposable
{
    private static readonly DateTime From = new(2026, 1, 15, 10, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime To = new(2026, 1, 15, 12, 0, 0, DateTimeKind.Utc);

    // Methods that build no Flux: writes, lifecycle, and the delete-API paths (a different syntax).
    private static readonly HashSet<string> NotQueries = new(StringComparer.Ordinal)
    {
        nameof(MonitoringInfluxClient.ReconfigureAsync),
        nameof(MonitoringInfluxClient.CheckHealthAsync),
        nameof(MonitoringInfluxClient.BackfillSpeedTestResultsAsync),
        nameof(MonitoringInfluxClient.SynchronizeSpeedTestMutationAsync),
        nameof(MonitoringInfluxClient.ClearSpeedTestHistoryAsync),
        nameof(MonitoringInfluxClient.FlushAsync),
        nameof(MonitoringInfluxClient.DisposeAsync),
        nameof(MonitoringInfluxClient.DisposeOwnedAsync),
    };

    private readonly HttpListener _influx = new();
    private readonly string _url;
    private readonly List<string> _queries = new();

    public MonitoringInfluxFluxGoldenTests()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();

        _url = $"http://localhost:{port}/";
        _influx.Prefixes.Add(_url);
        _influx.Start();
        _ = AnswerAsync();
    }

    public void Dispose() => _influx.Close();

    private async Task AnswerAsync()
    {
        while (_influx.IsListening)
        {
            HttpListenerContext context;
            try { context = await _influx.GetContextAsync(); }
            catch { return; }

            var path = context.Request.Url?.AbsolutePath ?? "";
            if (path.EndsWith("/api/v2/query", StringComparison.Ordinal))
            {
                using var reader = new StreamReader(context.Request.InputStream, Encoding.UTF8);
                var body = await reader.ReadToEndAsync();
                using var json = JsonDocument.Parse(body);
                lock (_queries) _queries.Add(json.RootElement.GetProperty("query").GetString() ?? "");
                context.Response.StatusCode = 200;
                context.Response.ContentType = "text/csv";
            }
            else if (path.EndsWith("/ping", StringComparison.Ordinal))
            {
                context.Response.StatusCode = 204;
            }
            else
            {
                var bytes = Encoding.UTF8.GetBytes("{}");
                context.Response.StatusCode = 200;
                context.Response.ContentType = "application/json";
                await context.Response.OutputStream.WriteAsync(bytes);
            }
            context.Response.Close();
        }
    }

    private sealed class TestDbFactory : IDbContextFactory<NetworkOptimizerDbContext>
    {
        private readonly DbContextOptions<NetworkOptimizerDbContext> _options;
        public TestDbFactory(DbContextOptions<NetworkOptimizerDbContext> options) => _options = options;
        public NetworkOptimizerDbContext CreateDbContext() => new(_options);
    }

    private sealed class NoopCredentialProtection : ICredentialProtectionService
    {
        public string Encrypt(string plaintext) => plaintext;
        public string Decrypt(string encrypted) => encrypted;
        public bool IsEncrypted(string? value) => false;
        public void EnsureKeyExists() { }
    }

    private async Task<MonitoringInfluxClient> ConfiguredClientAsync()
    {
        var options = new DbContextOptionsBuilder<NetworkOptimizerDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        await using (var db = new NetworkOptimizerDbContext(options))
        {
            db.MonitoringSettings.Add(new MonitoringSettings
            {
                InfluxDbUrl = _url,
                InfluxDbOrg = "network-optimizer",
                InfluxDbToken = "test-token",
                InfluxDbBucket = "network_monitoring",
                InfluxDbLongtermBucket = "network_monitoring_longterm",
            });
            await db.SaveChangesAsync();
        }

        var client = new MonitoringInfluxClient(new TestDbFactory(options), new NoopCredentialProtection(),
            NullLogger<MonitoringInfluxClient>.Instance);
        (await client.ReconfigureAsync(refreshSpeedTestBackfill: false)).Should().BeTrue();
        return client;
    }

    internal static IEnumerable<MethodInfo> QueryMethods() =>
        typeof(MonitoringInfluxClient)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(m => typeof(Task).IsAssignableFrom(m.ReturnType))
            .Where(m => !m.Name.StartsWith("Write", StringComparison.Ordinal) && !NotQueries.Contains(m.Name))
            .OrderBy(m => m.Name, StringComparer.Ordinal)
            .ThenBy(m => m.GetParameters().Length);

    private static string GoldenKey(MethodInfo m, string variant) =>
        $"{m.Name}.{m.GetParameters().Length}.{variant}";

    [Fact]
    public async Task Every_query_method_matches_its_golden_flux()
    {
        var update = Environment.GetEnvironmentVariable("NO_UPDATE_FLUX_GOLDENS") == "1";
        var dir = GoldenDirectory();
        Directory.CreateDirectory(dir);

        var client = await ConfiguredClientAsync();
        var mismatches = new List<string>();
        var silent = new List<string>();
        try
        {
            foreach (var method in QueryMethods())
            {
                foreach (var variant in new[] { "filled", "required" })
                {
                    var key = GoldenKey(method, variant);
                    var text = await CaptureAsync(client, method, fillOptional: variant == "filled");
                    if (text.Length == 0) silent.Add(key);

                    var file = Path.Combine(dir, key + ".flux");
                    if (update)
                    {
                        await File.WriteAllTextAsync(file, text);
                        continue;
                    }
                    if (!File.Exists(file))
                    {
                        mismatches.Add($"{key}: no golden file");
                        continue;
                    }
                    var golden = Normalize(await File.ReadAllTextAsync(file));
                    if (golden != text)
                        mismatches.Add($"{key}:\n--- golden\n{golden}\n--- actual\n{text}");
                }
            }
        }
        finally
        {
            await client.DisposeOwnedAsync();
        }

        mismatches.Should().BeEmpty();
        silent.Where(k => k.EndsWith(".filled", StringComparison.Ordinal)).Should().BeEmpty(
            "every query method should send at least one query when all its inputs are given");
    }

    [Fact]
    public async Task Golden_capture_is_stable_across_runs()
    {
        var client = await ConfiguredClientAsync();
        try
        {
            foreach (var method in QueryMethods())
            {
                var first = await CaptureAsync(client, method, fillOptional: true);
                var second = await CaptureAsync(client, method, fillOptional: true);
                second.Should().Be(first, method.Name);
            }
        }
        finally
        {
            await client.DisposeOwnedAsync();
        }
    }

    private const string Injection =
        "x\" or true)\nfrom(bucket: \"other-network_monitoring\") |> to(bucket: \"${b}\") //\\";

    public static TheoryData<string> InjectionSinks() => new()
    {
        nameof(MonitoringInfluxClient.QueryCustomOidFieldsAsync),
        nameof(MonitoringInfluxClient.QueryLatencyDetailByTargetIdAsync),
        nameof(MonitoringInfluxClient.FindRecentLossEventAsync),
        nameof(MonitoringInfluxClient.QueryPortByteUsageAsync),
    };

    [Theory]
    [MemberData(nameof(InjectionSinks))]
    public async Task Injected_value_stays_one_literal(string sink)
    {
        var client = await ConfiguredClientAsync();
        try
        {
            lock (_queries) _queries.Clear();
            var task = sink switch
            {
                nameof(MonitoringInfluxClient.QueryCustomOidFieldsAsync) =>
                    (Task)client.QueryCustomOidFieldsAsync("aa:bb:cc:dd:ee:ff", new[] { Injection }, From, To),
                nameof(MonitoringInfluxClient.QueryLatencyDetailByTargetIdAsync) =>
                    client.QueryLatencyDetailByTargetIdAsync(Injection, From, To),
                nameof(MonitoringInfluxClient.FindRecentLossEventAsync) =>
                    client.FindRecentLossEventAsync(To, From, new[] { Injection }),
                _ => client.QueryPortByteUsageAsync("aa:bb:cc:dd:ee:ff", new[] { Injection }, From, To, TimeSpan.FromHours(1)),
            };
            await task;

            List<string> sent;
            lock (_queries) sent = _queries.ToList();
            sent.Should().NotBeEmpty();
            foreach (var flux in sent)
            {
                // Outside string literals the query keeps one from() and gains no to().
                var code = Regex.Replace(flux, @"""(?:\\.|[^""\\])*""", "\"\"");
                Regex.Matches(code, @"\bfrom\(").Count.Should().Be(1, flux);
                code.Should().NotContain("to(", flux);
                flux.Should().Contain(FluxLiteral.Quote(Injection));
                Regex.IsMatch(flux, @"(?<!\\)\$\{").Should().BeFalse(flux);
            }
        }
        finally
        {
            await client.DisposeOwnedAsync();
        }
    }

    private async Task<string> CaptureAsync(MonitoringInfluxClient client, MethodInfo method, bool fillOptional)
    {
        lock (_queries) _queries.Clear();
        var args = method.GetParameters().Select(p => ArgumentFor(p, fillOptional)).ToArray();
        var task = (Task)method.Invoke(client, args)!;
        var done = await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(20)));
        done.Should().BeSameAs(task, $"{method.Name} should finish against an empty Influx");
        await task;

        List<string> sent;
        lock (_queries) sent = _queries.Select(Normalize).OrderBy(q => q, StringComparer.Ordinal).ToList();
        return string.Join("\n----\n", sent);
    }

    // Instants near the real clock come from the method itself (UtcNow), not from the inputs.
    private static readonly Regex Instant = new(@"\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(\.\d+)?Z", RegexOptions.CultureInvariant);

    private static string Normalize(string flux)
    {
        var text = flux.Replace("\r\n", "\n");
        return Instant.Replace(text, m =>
            DateTime.TryParse(m.Value, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AdjustToUniversal, out var t)
            && Math.Abs((t - DateTime.UtcNow).TotalDays) < 60
                ? "<now>"
                : m.Value);
    }

    private static object? ArgumentFor(ParameterInfo p, bool fillOptional)
    {
        var type = Nullable.GetUnderlyingType(p.ParameterType) ?? p.ParameterType;
        if (type == typeof(CancellationToken)) return CancellationToken.None;
        // Two interfaces in the filled variant are only valid as an explicit sum.
        if (fillOptional && p.Name == "sumAcrossInterfaces") return true;
        if (p.HasDefaultValue && (!fillOptional || type == typeof(bool)))
            return p.DefaultValue;

        var name = p.Name ?? "";
        if (type == typeof(string)) return StringFor(name);
        if (type == typeof(DateTime)) return IsEnd(name) ? To : From;
        if (type == typeof(TimeSpan)) return name.Contains("window", StringComparison.OrdinalIgnoreCase)
            ? TimeSpan.FromMinutes(5) : TimeSpan.FromHours(1);
        if (type == typeof(int)) return 7;
        if (type == typeof(long)) return 7L;
        if (type == typeof(double)) return 1.5;
        if (type == typeof(bool)) return false;
        if (type.IsEnum) return Enum.GetValues(type).GetValue(0);
        if (type == typeof(MonitoringInfluxClient.LatencyWanScope))
            return new MonitoringInfluxClient.LatencyWanScope(true, new[] { "wan1", "wan2" });

        var element = EnumerableElement(type);
        if (element == typeof(string))
        {
            var one = StringFor(name);
            // The required variant is the minimal call: one value per list.
            var values = fillOptional ? new List<string> { one, StringFor(name, second: true) } : new List<string> { one };
            return type.IsArray ? values.ToArray() : values;
        }
        if (element == typeof(int))
            return type.IsArray ? new[] { 1, 2 } : new List<int> { 1, 2 };
        if (element == typeof((string, string)))
            return name.Contains("exclude", StringComparison.OrdinalIgnoreCase)
                ? new List<(string, string)> { ("network_mode", "LTE") }
                : new List<(string, string)> { ("cm_id", "3"), ("device_mac", "AA-BB-CC-DD-EE-FF") };

        throw new NotSupportedException($"No golden input for {p.Member.Name}({p.ParameterType} {p.Name})");
    }

    private static Type? EnumerableElement(Type type)
    {
        if (type.IsArray) return type.GetElementType();
        if (!type.IsGenericType) return null;
        var def = type.GetGenericTypeDefinition();
        if (def == typeof(IEnumerable<>) || def == typeof(IReadOnlyList<>) || def == typeof(IReadOnlyCollection<>)
            || def == typeof(List<>) || def == typeof(IList<>) || def == typeof(ICollection<>)
            || def == typeof(ISet<>) || def == typeof(HashSet<>) || def == typeof(IReadOnlySet<>))
            return type.GetGenericArguments()[0];
        return null;
    }

    private static bool IsEnd(string name) =>
        name.Equals("to", StringComparison.OrdinalIgnoreCase)
        || name.Equals("end", StringComparison.OrdinalIgnoreCase)
        || name.EndsWith("To", StringComparison.Ordinal)
        || name.EndsWith("End", StringComparison.Ordinal)
        || name.Equals("until", StringComparison.OrdinalIgnoreCase);

    // Realistic values: MACs, VLAN interfaces, OID field names, target ids, WAN groups.
    private static string StringFor(string name, bool second = false)
    {
        var n = name.ToLowerInvariant();
        if (n.Contains("mac")) return second ? "00:11:22:33:44:55" : "aa:bb:cc:dd:ee:ff";
        if (n.Contains("ifname") || n.Contains("interface")) return second ? "eth8" : "eth0.10";
        if (n.Contains("field")) return second ? "if_hc_in_octets" : "custom_oid_1";
        if (n.Contains("target") && n.Contains("type")) return "isp_transit";
        if (n.Contains("target")) return second ? "8.8.8.8" : "1.1.1.1";
        if (n.Contains("measurement")) return "wifi_client";
        if (n.Contains("band")) return second ? "na" : "ng";
        if (n.Contains("wan")) return second ? "WAN2" : "WAN";
        if (n.Contains("module") || n.Contains("modem") || n.Contains("dish") || n.Contains("ont"))
            return second ? "module-2" : "module-1";
        if (n.Contains("host")) return "speed.example.com";
        if (n.Contains("edge")) return "last";
        return second ? $"{name}-2" : $"{name}-1";
    }

    private static string GoldenDirectory([CallerFilePath] string source = "") =>
        Path.Combine(Path.GetDirectoryName(source)!, "Goldens", "MonitoringInfluxFlux");
}
