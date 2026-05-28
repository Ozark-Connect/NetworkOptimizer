namespace NetworkOptimizer.Core.Helpers;

/// <summary>
/// 33-color chart palette: Observable 10 + Tableau + D3 Paired + D3 Set3,
/// with perceptually close pairs (deltaE &lt; 11) culled.
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
        // Tableau (8)
        "#4e79a7", "#f28e2c", "#e15759", "#76b7b2",
        "#edc949", "#af7aa1", "#ff9da7", "#d1b894",
        // D3 Paired (8)
        "#a6cee3", "#b2df8a", "#33a02c", "#e31a1c",
        "#fdbf6f", "#ff7f00", "#cab2d6", "#6a3d9a",
        // D3 Set3 (7)
        "#fb8072", "#80b1d3", "#b3de69", "#fccde5",
        "#bc80bd", "#ccebc5", "#b5508c"
    ];
}
