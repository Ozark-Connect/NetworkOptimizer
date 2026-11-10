using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

namespace NetworkOptimizer.Web.Tests;

/// <summary>
/// Every decimal parse in src names its culture. A bare <c>double.TryParse(s, out v)</c> follows the
/// host's locale, so on a comma-decimal install (de-DE Windows or macOS) "-11.5" from a device reads
/// as -115 and lands in telemetry and alert thresholds (#1373). Machine text takes
/// <c>CultureInfo.InvariantCulture</c>; text a person typed in their own locale passes
/// <c>CultureInfo.CurrentCulture</c>, which says so on purpose.
/// </summary>
public class NumberParsingArchitectureTests
{
    private static readonly Regex ParseCall = new(
        @"\b(?:double|float|decimal|Double|Single|Decimal)\.(?:Try)?Parse\(", RegexOptions.Compiled);

    // A provider argument is any of these; the call's own text is checked, not the whole line.
    private static readonly Regex Provider = new(
        @"CultureInfo|NumberFormatInfo|IFormatProvider|\bprovider\b|\bculture\b", RegexOptions.Compiled);

    [Fact]
    public void Every_decimal_parse_in_src_names_its_culture()
    {
        var src = FindSrcRoot();
        var offenders = new List<string>();

        foreach (var file in SourceFiles(src))
        {
            var text = File.ReadAllText(file);
            foreach (Match m in ParseCall.Matches(text))
            {
                var args = CallArguments(text, m.Index + m.Length);
                if (Provider.IsMatch(args)) continue;

                var line = text.AsSpan(0, m.Index).Count('\n') + 1;
                offenders.Add($"{Path.GetRelativePath(src, file)}:{line}");
            }
        }

        offenders.Should().BeEmpty(
            "a parse without a culture follows the host locale - pass "
            + "NumberStyles.Float, CultureInfo.InvariantCulture for machine text, or "
            + "CultureInfo.CurrentCulture for text a person typed");
    }

    [Theory]
    [InlineData("double.TryParse(s, out var v)", false)]
    [InlineData("double.Parse(parts[1])", false)]
    [InlineData("decimal.TryParse(Get(\"x\"), out var d)", false)]
    [InlineData("double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v)", true)]
    [InlineData("double.TryParse(\n    s,\n    System.Globalization.NumberStyles.Float,\n    System.Globalization.CultureInfo.InvariantCulture,\n    out var v)", true)]
    [InlineData("float.Parse(s, provider)", true)]
    public void The_scanner_reads_the_call_not_the_line(string code, bool hasProvider)
    {
        var m = ParseCall.Match(code);
        m.Success.Should().BeTrue();
        Provider.IsMatch(CallArguments(code, m.Index + m.Length)).Should().Be(hasProvider);
    }

    /// <summary>The text between the call's opening paren and its matching close, across lines.</summary>
    private static string CallArguments(string text, int start)
    {
        var depth = 1;
        for (var i = start; i < text.Length; i++)
        {
            if (text[i] == '(') depth++;
            else if (text[i] == ')' && --depth == 0) return text[start..i];
        }
        return text[start..];
    }

    private static IEnumerable<string> SourceFiles(string src)
        => Directory.EnumerateFiles(src, "*.*", SearchOption.AllDirectories)
            .Where(f => f.EndsWith(".cs", StringComparison.Ordinal) || f.EndsWith(".razor", StringComparison.Ordinal))
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                        && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                        && !f.Contains($"{Path.DirectorySeparatorChar}Migrations{Path.DirectorySeparatorChar}"));

    /// <summary>Walks up from the test assembly to the repo's src folder, so this works from any runner.</summary>
    private static string FindSrcRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "src");
            if (Directory.Exists(Path.Combine(candidate, "NetworkOptimizer.Web")))
                return candidate;
            dir = dir.Parent;
        }

        throw new InvalidOperationException("Could not locate src/ from the test output directory.");
    }
}
