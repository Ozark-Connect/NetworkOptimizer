using System.Diagnostics;
using FluentAssertions;
using NetworkOptimizer.Sqm.Models;
using Xunit;

namespace NetworkOptimizer.Sqm.Tests;

/// <summary>
/// Every script we hand a gateway must at least parse. Runs <c>bash -n</c> over the boot script,
/// the speedtest and ping scripts it embeds, and the shaper-lift wrapper, for a static and a
/// dynamic-upload configuration. Skipped silently where no bash is on the PATH.
/// </summary>
public class GeneratedScriptSyntaxTests
{
    private static string? BashPath()
    {
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            foreach (var name in new[] { "bash", "bash.exe" })
            {
                var candidate = Path.Combine(dir, name);
                if (File.Exists(candidate)) return candidate;
            }
        }
        return null;
    }

    private static SqmConfiguration Config(double uploadSeverity)
    {
        var config = new SqmConfiguration
        {
            ConnectionType = ConnectionType.CellularHome,
            ConnectionName = "Test WAN",
            Interface = "eth6.228",
            NominalDownloadSpeed = 200,
            NominalUploadSpeed = 30,
            ShapeUpload = true,
            PingHost = "1.1.1.1",
            UploadCongestionSeverity = uploadSeverity,
        };
        config.ApplyProfileSettings(1000);
        return config;
    }

    private static string Embedded(string boot, string marker)
    {
        var start = boot.IndexOf($"<< '{marker}'\n", StringComparison.Ordinal);
        start.Should().BeGreaterThan(0, $"the boot script embeds a {marker} heredoc");
        start = boot.IndexOf('\n', start) + 1;
        var end = boot.IndexOf($"\n{marker}\n", start, StringComparison.Ordinal);
        end.Should().BeGreaterThan(start);
        return boot[start..end];
    }

    // The script goes in over stdin: a WSL bash cannot open a Windows temp path, and Git Bash
    // can, so stdin is the one route that works for whichever bash the PATH offers.
    private static void AssertParses(string bash, string script, string label)
    {
        var psi = new ProcessStartInfo(bash, "-n")
        {
            RedirectStandardInput = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        using var proc = Process.Start(psi)!;
        proc.StandardInput.NewLine = "\n";
        proc.StandardInput.Write(script.Replace("\r\n", "\n"));
        proc.StandardInput.Close();
        var stderr = proc.StandardError.ReadToEnd();
        proc.WaitForExit(30_000);
        proc.ExitCode.Should().Be(0, $"{label} must parse: {stderr}");
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(0.5)]
    public void BootSpeedtestAndPingScripts_ParseUnderBash(double uploadSeverity)
    {
        var bash = BashPath();
        if (bash == null) return;

        var config = Config(uploadSeverity);
        var profile = config.GetProfile();
        var boot = new ScriptGenerator(config)
            .GenerateAllScripts(profile.GetHourlyBaseline(1.0), profile.GetHourlyUploadBaseline(uploadSeverity))
            .Values.Single()
            .Replace("\r\n", "\n");

        AssertParses(bash, boot, "boot script");
        AssertParses(bash, Embedded(boot, "SPEEDTEST_EOF"), "speedtest script");
        AssertParses(bash, Embedded(boot, "PING_EOF"), "ping script");
    }

    [Fact]
    public void ShaperLiftWrapper_ParsesUnderBash()
    {
        var bash = BashPath();
        if (bash == null) return;

        var wrapper = SqmShaperLiftScript.Wrap("/data/uwnspeedtest --interface eth6.228 -streams 10 -servers 4 -duration 4", "eth6.228", 273, 33, true);
        AssertParses(bash, wrapper, "shaper-lift wrapper");
    }
}
