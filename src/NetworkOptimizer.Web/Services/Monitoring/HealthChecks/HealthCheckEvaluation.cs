using System.Globalization;
using System.Text.RegularExpressions;
using NetworkOptimizer.Storage.Models;

namespace NetworkOptimizer.Web.Services.Monitoring.HealthChecks;

/// <summary>
/// The pure half of a health check: reading one value out of command output and judging it
/// against the condition. No SSH, no state, so every rule here is testable against captures.
/// </summary>
public static class HealthCheckEvaluation
{
    /// <summary>Marker the runner appends so the user command's exit code rides in the output.</summary>
    public const string ExitMarker = "###HC_EXIT=";

    /// <summary>Field names are Influx field keys: lowercase letters, digits and underscores.</summary>
    public static readonly Regex FieldNamePattern = new(@"^[a-z0-9_]{1,60}$", RegexOptions.Compiled);

    /// <summary>Influx field prefix for check values, so they never collide with a custom OID field.</summary>
    public const string FieldPrefix = "hc_";

    /// <summary>Influx field name for a check's value.</summary>
    public static string ValueField(string fieldName) => FieldPrefix + fieldName;

    /// <summary>Influx field name for a check's pass/fail (1 = condition holds, i.e. failing).</summary>
    public static string FailingField(string fieldName) => FieldPrefix + fieldName + "_failing";

    /// <summary>
    /// Wraps the user's command so its exit code arrives on the last line and the SSH layer
    /// always sees a zero exit, since it reports a non-zero exit as a failed run.
    /// </summary>
    public static string WrapCommand(string command) =>
        $"( {command.TrimEnd().TrimEnd(';')} ); echo \"{ExitMarker}$?\"";

    /// <summary>Splits the wrapped output into the user command's output and its exit code.</summary>
    public static (string Output, int? ExitCode) Unwrap(string? raw)
    {
        if (string.IsNullOrEmpty(raw)) return (string.Empty, null);
        var idx = raw.LastIndexOf(ExitMarker, StringComparison.Ordinal);
        if (idx < 0) return (raw.TrimEnd(), null);

        var codeText = raw[(idx + ExitMarker.Length)..].Trim();
        var output = raw[..idx].TrimEnd('\r', '\n');
        return (output, int.TryParse(codeText, out var code) ? code : null);
    }

    /// <summary>
    /// True when the output says this check does not apply on this device. With no pattern that
    /// is an empty output; with one, any line matching it.
    /// </summary>
    public static bool IsNotApplicable(string output, string? pattern)
    {
        if (string.IsNullOrWhiteSpace(pattern)) return string.IsNullOrWhiteSpace(output);
        try
        {
            return Regex.IsMatch(output, pattern, RegexOptions.Multiline, TimeSpan.FromSeconds(1));
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static readonly Regex NumberPattern = new(@"-?\d+(?:\.\d+)?", RegexOptions.Compiled);

    /// <summary>Reads the check's value out of the output. Null when nothing usable is there.</summary>
    public static double? Parse(string output, int? exitCode, HealthCheckParser parser, string? parserArg)
    {
        switch (parser)
        {
            case HealthCheckParser.ExitCode:
                return exitCode;

            case HealthCheckParser.FirstNumber:
            {
                var line = Lines(output).LastOrDefault(l => l.Trim().Length > 0);
                if (line == null) return null;
                var m = NumberPattern.Match(line);
                return m.Success && double.TryParse(m.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : null;
            }

            case HealthCheckParser.RegexGroup:
            {
                var regex = Compile(parserArg);
                if (regex == null) return null;
                foreach (var line in Lines(output).Reverse())
                {
                    var m = regex.Match(line);
                    if (!m.Success) continue;
                    var text = m.Groups.Count > 1 ? m.Groups[1].Value : m.Value;
                    var n = NumberPattern.Match(text);
                    if (n.Success && double.TryParse(n.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
                        return v;
                }
                return null;
            }

            case HealthCheckParser.MatchingLineCount:
            {
                var regex = Compile(parserArg);
                if (regex == null) return null;
                return Lines(output).Count(l => regex.IsMatch(l));
            }

            default:
                return null;
        }
    }

    /// <summary>Whether the condition holds, i.e. the check is failing.</summary>
    public static bool ConditionHolds(double value, HealthCheckOperator op, double threshold) => op switch
    {
        HealthCheckOperator.GreaterOrEqual => value >= threshold,
        HealthCheckOperator.LessOrEqual => value <= threshold,
        HealthCheckOperator.Equal => Math.Abs(value - threshold) < 1e-9,
        HealthCheckOperator.NotEqual => Math.Abs(value - threshold) >= 1e-9,
        _ => false,
    };

    /// <summary>The operator as it reads in a sentence ("at or above 25").</summary>
    public static string DescribeCondition(HealthCheckOperator op, double threshold)
    {
        var t = threshold.ToString("0.##", CultureInfo.InvariantCulture);
        return op switch
        {
            HealthCheckOperator.GreaterOrEqual => $"at or above {t}",
            HealthCheckOperator.LessOrEqual => $"at or below {t}",
            HealthCheckOperator.Equal => $"equal to {t}",
            HealthCheckOperator.NotEqual => $"anything but {t}",
            _ => t,
        };
    }

    /// <summary>The operator's symbol, for the editor's dropdown and the chart legend.</summary>
    public static string OperatorSymbol(HealthCheckOperator op) => op switch
    {
        HealthCheckOperator.GreaterOrEqual => ">=",
        HealthCheckOperator.LessOrEqual => "<=",
        HealthCheckOperator.Equal => "=",
        HealthCheckOperator.NotEqual => "!=",
        _ => "?",
    };

    /// <summary>A field name derived from a display name: lowercase, underscores, trimmed to fit.</summary>
    public static string SlugifyFieldName(string name)
    {
        var slug = Regex.Replace(name.ToLowerInvariant(), @"[^a-z0-9]+", "_").Trim('_');
        if (slug.Length > 60) slug = slug[..60].TrimEnd('_');
        return slug.Length == 0 ? "check" : slug;
    }

    private static Regex? Compile(string? pattern)
    {
        if (string.IsNullOrWhiteSpace(pattern)) return null;
        try
        {
            return new Regex(pattern, RegexOptions.None, TimeSpan.FromSeconds(1));
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    private static string[] Lines(string output) =>
        output.Split('\n').Select(l => l.TrimEnd('\r')).ToArray();
}
