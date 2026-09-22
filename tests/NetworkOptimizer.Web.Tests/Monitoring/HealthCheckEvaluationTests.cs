using FluentAssertions;
using NetworkOptimizer.Core.Enums;
using NetworkOptimizer.Storage.Models;
using NetworkOptimizer.Web.Services.Monitoring.HealthChecks;
using Xunit;

namespace NetworkOptimizer.Web.Tests.Monitoring;

public class HealthCheckEvaluationTests
{
    [Fact]
    public void Unwrap_splits_output_from_the_exit_marker()
    {
        var (output, code) = HealthCheckEvaluation.Unwrap("4.8 4\n###HC_EXIT=0\n");
        output.Should().Be("4.8 4");
        code.Should().Be(0);
    }

    [Fact]
    public void Unwrap_without_a_marker_keeps_the_output_and_no_code()
    {
        var (output, code) = HealthCheckEvaluation.Unwrap("Connection refused");
        output.Should().Be("Connection refused");
        code.Should().BeNull();
    }

    [Fact]
    public void WrapCommand_runs_the_user_command_in_a_subshell_and_echoes_its_exit()
    {
        HealthCheckEvaluation.WrapCommand("false;")
            .Should().Be("( false ); echo \"###HC_EXIT=$?\"");
    }

    [Theory]
    [InlineData("4.8 4", 4.8)]
    [InlineData("header\n  65.0 55  ", 65.0)]
    [InlineData("used 91%", 91)]
    [InlineData("-3", -3)]
    public void FirstNumber_reads_the_first_number_on_the_last_non_empty_line(string output, double expected)
    {
        HealthCheckEvaluation.Parse(output, 0, HealthCheckParser.FirstNumber, null).Should().Be(expected);
    }

    [Fact]
    public void FirstNumber_returns_null_when_the_last_line_has_no_number()
    {
        HealthCheckEvaluation.Parse("nothing here", 0, HealthCheckParser.FirstNumber, null).Should().BeNull();
    }

    [Fact]
    public void RegexGroup_takes_the_first_group_of_the_last_matching_line()
    {
        var output = "swap used=12 MB\nswap used=340 MB\ntotal";
        HealthCheckEvaluation.Parse(output, 0, HealthCheckParser.RegexGroup, @"used=(\d+)").Should().Be(340);
    }

    [Fact]
    public void RegexGroup_with_an_invalid_pattern_yields_null()
    {
        HealthCheckEvaluation.Parse("x", 0, HealthCheckParser.RegexGroup, "(").Should().BeNull();
    }

    [Fact]
    public void MatchingLineCount_counts_lines()
    {
        var output = "ERROR a\nok\nERROR b\n";
        HealthCheckEvaluation.Parse(output, 0, HealthCheckParser.MatchingLineCount, "^ERROR").Should().Be(2);
    }

    [Fact]
    public void ExitCode_parser_returns_the_code()
    {
        HealthCheckEvaluation.Parse("", 3, HealthCheckParser.ExitCode, null).Should().Be(3);
        HealthCheckEvaluation.Parse("", null, HealthCheckParser.ExitCode, null).Should().BeNull();
    }

    [Theory]
    [InlineData(25, HealthCheckOperator.GreaterOrEqual, 25, true)]
    [InlineData(24.9, HealthCheckOperator.GreaterOrEqual, 25, false)]
    [InlineData(5, HealthCheckOperator.LessOrEqual, 10, true)]
    [InlineData(0, HealthCheckOperator.Equal, 0, true)]
    [InlineData(1, HealthCheckOperator.NotEqual, 0, true)]
    public void ConditionHolds_compares_as_the_operator_says(double value, HealthCheckOperator op, double threshold, bool expected)
    {
        HealthCheckEvaluation.ConditionHolds(value, op, threshold).Should().Be(expected);
    }

    [Fact]
    public void NotApplicable_is_an_empty_output_when_no_pattern_is_set()
    {
        HealthCheckEvaluation.IsNotApplicable("", null).Should().BeTrue();
        HealthCheckEvaluation.IsNotApplicable("  \n", null).Should().BeTrue();
        HealthCheckEvaluation.IsNotApplicable("0.0 0", null).Should().BeFalse();
    }

    [Fact]
    public void NotApplicable_matches_the_pattern_on_any_line()
    {
        HealthCheckEvaluation.IsNotApplicable("NA", "^NA$").Should().BeTrue();
        HealthCheckEvaluation.IsNotApplicable("0.0 0", "^NA$").Should().BeFalse();
    }

    [Fact]
    public void SlugifyFieldName_makes_an_influx_safe_key()
    {
        HealthCheckEvaluation.SlugifyFieldName("UniFi Network JVM GC Thrash").Should().Be("unifi_network_jvm_gc_thrash");
        HealthCheckEvaluation.FieldNamePattern.IsMatch(HealthCheckEvaluation.SlugifyFieldName("!!!")).Should().BeTrue();
    }

    [Fact]
    public void The_jvm_template_condition_separates_thrash_from_healthy()
    {
        // Values the verified awk check produces: a healthy gateway and Jake's excerpt.
        HealthCheckEvaluation.ConditionHolds(0.4, HealthCheckOperator.GreaterOrEqual, 25).Should().BeFalse();
        HealthCheckEvaluation.ConditionHolds(65, HealthCheckOperator.GreaterOrEqual, 25).Should().BeTrue();
    }
}

public class HealthCheckTemplateFitTests
{
    private static HealthCheckTemplate CloudOnly() => new() { AppliesTo = ["cloud-gateway"] };

    [Fact]
    public void Cloud_gateway_template_fits_a_udm_and_a_ucg_fiber()
    {
        CloudOnly().Fits(DeviceType.Gateway, "UDMPRO", null).Should().BeTrue();
        CloudOnly().Fits(DeviceType.Gateway, null, "UCG-Fiber").Should().BeTrue();
    }

    [Fact]
    public void Cloud_gateway_template_is_withheld_from_a_uxg_and_from_unknown_hardware()
    {
        CloudOnly().Fits(DeviceType.Gateway, "UXGPRO", null).Should().BeFalse();
        CloudOnly().Fits(DeviceType.Gateway, null, null).Should().BeFalse();
        CloudOnly().Fits(DeviceType.AccessPoint, "UDMPRO", null).Should().BeFalse();
    }

    [Fact]
    public void Plain_gateway_and_empty_scopes_fit_any_gateway()
    {
        new HealthCheckTemplate { AppliesTo = ["gateway"] }.Fits(DeviceType.Gateway, "UXGPRO", null).Should().BeTrue();
        new HealthCheckTemplate().Fits(DeviceType.Switch).Should().BeTrue();
    }
}

public class HealthCheckRemediesTests
{
    [Theory]
    [InlineData("unifi", true)]
    [InlineData("netopt-agent.service", true)]
    [InlineData("unifi; rm -rf /", false)]
    [InlineData("$(reboot)", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Arguments_are_restricted_to_service_and_process_name_characters(string? arg, bool valid)
    {
        HealthCheckRemedies.IsValidArgument(arg).Should().Be(valid);
    }

    [Fact]
    public void BuildCommand_refuses_an_invalid_argument()
    {
        HealthCheckRemedies.BuildCommand(HealthCheckRemedy.RestartService, "a b").Should().BeNull();
        HealthCheckRemedies.BuildCommand(HealthCheckRemedy.RestartService, "unifi").Should().Be("systemctl restart unifi");
        HealthCheckRemedies.BuildCommand(HealthCheckRemedy.KillProcess, "iperf3").Should().Be("pkill -x iperf3");
        HealthCheckRemedies.BuildCommand(HealthCheckRemedy.None, null).Should().BeNull();
    }

    [Fact]
    public void RestartService_is_gateway_only()
    {
        HealthCheckRemedies.SupportedOn(HealthCheckRemedy.RestartService, DeviceType.Gateway).Should().BeTrue();
        HealthCheckRemedies.SupportedOn(HealthCheckRemedy.RestartService, DeviceType.AccessPoint).Should().BeFalse();
        HealthCheckRemedies.SupportedOn(HealthCheckRemedy.KillProcess, DeviceType.AccessPoint).Should().BeTrue();
        HealthCheckRemedies.SupportedOn(HealthCheckRemedy.RebootDevice, DeviceType.Switch).Should().BeTrue();
    }
}
