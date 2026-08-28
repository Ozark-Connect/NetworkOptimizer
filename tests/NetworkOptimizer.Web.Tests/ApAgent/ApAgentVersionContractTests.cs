using FluentAssertions;
using NetworkOptimizer.Web.Services.ApAgent;
using Xunit;

namespace NetworkOptimizer.Web.Tests.ApAgent;

/// <summary>
/// The contract version and the /health payload it is read from. The app and the Go binary embed
/// the same src/apagent/binary-version file, so a drift between them is the one way the redeploy
/// prompt can be wrong in both directions at once.
/// </summary>
public class ApAgentVersionContractTests
{
    [Fact]
    public void TheServerShipsTheSameContractVersion_the_go_module_embeds()
    {
        var repoRoot = FindRepositoryRoot();
        var expected = int.Parse(File.ReadAllText(Path.Combine(repoRoot, "src", "apagent", "binary-version")).Trim());

        ApAgentDeploymentService.ExpectedBinaryVersion.Should().Be(expected);
    }

    [Fact]
    public void TheEmbeddedWrapper_is_the_one_the_go_module_ships()
    {
        var repoRoot = FindRepositoryRoot();
        var onDisk = File.ReadAllText(Path.Combine(repoRoot, "src", "apagent", "apagent.sh")).Replace("\r\n", "\n");

        var embedded = ReadEmbedded("apagent.apagent.sh");

        embedded.Should().Be(onDisk);
        embedded.Should().StartWith("#!/bin/sh");
    }

    [Fact]
    public void AHealthBody_yields_the_fields_the_redeploy_decision_turns_on()
    {
        const string body = """
            {
              "version": "2.7.1",
              "binary_version": 4,
              "started_at": "2026-08-24T10:00:00Z",
              "uptime_seconds": 7200,
              "degraded": true,
              "unavailable": ["stahtd", "athstats"],
              "last_probe_run": "2026-08-24T11:55:00Z",
              "collected_at": "2026-08-24T12:00:00Z"
            }
            """;

        var health = ApAgentHealthClient.ParseHealth(body);

        health.Should().NotBeNull();
        health!.Version.Should().Be("2.7.1");
        health.BinaryVersion.Should().Be(4);
        health.Degraded.Should().BeTrue();
        health.Unavailable.Should().BeEquivalentTo("stahtd", "athstats");
        (health.CollectedAt - health.LastProbeRun).Should().Be(TimeSpan.FromMinutes(5));
    }

    [Fact]
    public void AResponseFromSomethingElseOnPort8899_is_not_read_as_a_healthy_agent()
    {
        ApAgentHealthClient.ParseHealth("""{"status":"ok"}""").Should().BeNull();
        ApAgentHealthClient.ParseHealth("<html>not json</html>").Should().BeNull();
        ApAgentHealthClient.ParseHealth("[]").Should().BeNull();
    }

    private static string ReadEmbedded(string name)
    {
        var assembly = typeof(ApAgentDeploymentService).Assembly;
        using var stream = assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"Embedded resource {name} is missing from the build.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd().Replace("\r\n", "\n");
    }

    private static string FindRepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "NetworkOptimizer.sln")))
            dir = dir.Parent;

        return dir?.FullName ?? throw new InvalidOperationException("Could not locate the repository root.");
    }
}
