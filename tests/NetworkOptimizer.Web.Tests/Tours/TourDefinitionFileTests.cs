using System.Reflection;
using System.Text.Json;
using FluentAssertions;
using NetworkOptimizer.Web.Services.Tours;
using Xunit;

namespace NetworkOptimizer.Web.Tests.Tours;

/// <summary>
/// Guards the shipped tour JSON against the mistake it cannot report: a step whose "requires"
/// names a predicate that does not exist resolves to "no site qualifies", so the step is silently
/// dropped from every install forever, with nothing in the logs to say a tour lost a step.
/// </summary>
public class TourDefinitionFileTests
{
    private static readonly HashSet<string> KnownPredicates = typeof(TourPredicateResolver)
        .GetFields(BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy)
        .Where(f => f.IsLiteral && f.FieldType == typeof(string))
        .Select(f => (string)f.GetRawConstantValue()!)
        .ToHashSet(StringComparer.OrdinalIgnoreCase);

    public static TheoryData<string> TourFiles()
    {
        var data = new TheoryData<string>();
        foreach (var file in Directory.GetFiles(ToursDirectory(), "*.json"))
            data.Add(Path.GetFileName(file));
        return data;
    }

    [Theory]
    [MemberData(nameof(TourFiles))]
    public void EveryStepRequiresAKnownPredicate(string fileName)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(ToursDirectory(), fileName)));

        foreach (var step in doc.RootElement.GetProperty("steps").EnumerateArray())
        {
            if (!step.TryGetProperty("requires", out var requires))
                continue;

            foreach (var predicate in requires.EnumerateArray())
            {
                KnownPredicates.Should().Contain(predicate.GetString()!,
                    $"step '{step.GetProperty("id").GetString()}' in {fileName} would never be shown otherwise");
            }
        }
    }

    private static string ToursDirectory()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "NetworkOptimizer.sln")))
            directory = directory.Parent;

        directory.Should().NotBeNull("the test must run from inside a NetworkOptimizer checkout");
        return Path.Combine(directory!.FullName, "src", "NetworkOptimizer.Web", "wwwroot", "data", "tours");
    }
}
