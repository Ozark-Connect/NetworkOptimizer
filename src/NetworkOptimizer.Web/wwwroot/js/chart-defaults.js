// 40-color chart palette — Observable 10 + Tableau 10 + D3 Paired + D3 Set3.
// Source of truth for both Blazor-rendered ApexCharts (via window.Apex.colors)
// and JS module charts (via window.Apex.colors or direct import).
// Charts that set explicit Colors/colors override this default.
(function () {
    var palette = [
        // Observable 10 — modern, designed for data viz (2024)
        '#4269d0', '#efb118', '#ff725c', '#6cc5b0', '#3ca951',
        '#ff8ab7', '#a463f2', '#97bbf5', '#9c6b4e', '#9498a0',
        // Tableau 10 — proven, slightly more muted
        '#4e79a7', '#f28e2c', '#e15759', '#76b7b2', '#59a14f',
        '#edc949', '#af7aa1', '#ff9da7', '#9c755f', '#bab0ab',
        // D3 Paired — light/dark pairs, good range
        '#a6cee3', '#1f78b4', '#b2df8a', '#33a02c', '#fb9a99',
        '#e31a1c', '#fdbf6f', '#ff7f00', '#cab2d6', '#6a3d9a',
        // D3 Set3 — pleasant pastels
        '#8dd3c7', '#bebada', '#fb8072', '#80b1d3', '#fdb462',
        '#b3de69', '#fccde5', '#bc80bd', '#ccebc5', '#ffed6f'
    ];
    window.Apex = window.Apex || {};
    window.Apex.colors = palette;
})();
