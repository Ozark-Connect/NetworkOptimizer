namespace NetworkOptimizer.Core.Helpers;

/// <summary>
/// Grafana "Classic" palette. Single source of truth for C# components;
/// JS charts use the matching window.Apex.colors set by chart-defaults.js.
/// </summary>
public static class ChartPalette
{
    public static readonly string[] Colors =
    [
        "#7EB26D", "#EAB839", "#6ED0E0", "#EF843C", "#E24D42", "#1F78C1",
        "#BA43A9", "#705DA0", "#508642", "#CCA300", "#447EBC", "#C15C17",
        "#890F02", "#0A437C", "#6D1F62", "#584477", "#B7DBAB", "#F4D598",
        "#70DBED", "#F9BA8F", "#F29191", "#82B5D8", "#E5A8E2", "#AEA2E0"
    ];
}
