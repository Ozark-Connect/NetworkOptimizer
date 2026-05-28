namespace NetworkOptimizer.Core.Helpers;

/// <summary>
/// 33-color chart palette: Observable 10 base, extended with curated picks from
/// Tableau, D3 Paired, and hand-selected colors. All pairs have deltaE &gt; 16.
/// Single source of truth for C# components;
/// JS charts use the matching window.Apex.colors set by chart-defaults.js.
/// </summary>
public static class ChartPalette
{
    public static readonly string[] Colors =
    [
        // Observable 10
        "#4269d0", "#efb118", "#ff725c", "#6cc5b0", "#3ca951",
        "#ff8ab7", "#a463f2", "#97bbf5", "#9c6b4e", "#9498a0",
        // Tableau picks
        "#4e79a7", "#f28e2c", "#b22222",
        "#edc949", "#af7aa1", "#ff9da7", "#d1b894",
        // D3 Paired picks
        "#a6cee3", "#b2df8a", "#e31a1c",
        "#fdbf6f", "#cab2d6", "#6a3d9a",
        // Extended
        "#d94f70", "#ccebc5", "#b5508c",
        "#2d6a4f", "#c26d3a", "#3d8a8a", "#8b9a46",
        "#d4956b", "#8a6bbf", "#45b8d4"
    ];
}
