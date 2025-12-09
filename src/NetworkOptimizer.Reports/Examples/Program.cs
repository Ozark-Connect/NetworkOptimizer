using NetworkOptimizer.Reports;
using NetworkOptimizer.Reports.Examples;

namespace NetworkOptimizer.Reports.Examples;

/// <summary>
/// Sample console application demonstrating report generation
/// Run this to generate sample PDF and Markdown reports
/// </summary>
class Program
{
    static void Main(string[] args)
    {
        Console.WriteLine("NetworkOptimizer.Reports - Sample Report Generator");
        Console.WriteLine("====================================================");
        Console.WriteLine();

        var outputDirectory = args.Length > 0 ? args[0] : Directory.GetCurrentDirectory();
        Console.WriteLine($"Output directory: {outputDirectory}");
        Console.WriteLine();

        try
        {
            // Example 1: Complete sample report
            Console.WriteLine("Generating complete sample report...");
            SampleReportGeneration.GenerateCompleteSampleReport(outputDirectory);
            Console.WriteLine();

            // Example 2: White-label report
            Console.WriteLine("Generating white-label report...");
            SampleReportGeneration.GenerateWhiteLabelReport(outputDirectory, "Acme Corporation");
            Console.WriteLine();

            // Example 3: Minimal report
            Console.WriteLine("Generating minimal report...");
            var minimalData = SampleReportGeneration.BuildMinimalReport();
            var pdfGen = new PdfReportGenerator();
            pdfGen.GenerateReport(minimalData, Path.Combine(outputDirectory, "minimal_report.pdf"));
            Console.WriteLine($"Minimal PDF generated: {Path.Combine(outputDirectory, "minimal_report.pdf")}");
            Console.WriteLine();

            // Example 4: All issue types report
            Console.WriteLine("Generating report with all issue types...");
            var issuesData = SampleReportGeneration.BuildReportWithAllIssueTypes();
            var mdGen = new MarkdownReportGenerator();
            mdGen.GenerateReport(issuesData, Path.Combine(outputDirectory, "all_issues_report.md"));
            Console.WriteLine($"All issues MD generated: {Path.Combine(outputDirectory, "all_issues_report.md")}");
            Console.WriteLine();

            Console.WriteLine("====================================================");
            Console.WriteLine("All reports generated successfully!");
            Console.WriteLine();
            Console.WriteLine("Generated files:");
            Console.WriteLine("  - sample_network_audit.pdf");
            Console.WriteLine("  - sample_network_audit.md");
            Console.WriteLine("  - Acme_Corporation_audit.pdf");
            Console.WriteLine("  - minimal_report.pdf");
            Console.WriteLine("  - all_issues_report.md");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"ERROR: {ex.Message}");
            Console.WriteLine(ex.StackTrace);
            Environment.Exit(1);
        }
    }
}
