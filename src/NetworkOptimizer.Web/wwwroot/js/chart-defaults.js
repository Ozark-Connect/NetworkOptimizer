// 33-color chart palette — Observable 10 + Tableau + D3 Paired + D3 Set3,
// with perceptually close pairs (deltaE < 11) culled.
// Source of truth for both Blazor-rendered ApexCharts (via window.Apex.colors)
// and JS module charts (via window.Apex.colors or direct import).
// Charts that set explicit Colors/colors override this default.
(function () {
    var palette = [
        // Observable 10
        '#4269d0', '#efb118', '#ff725c', '#6cc5b0', '#3ca951',
        '#ff8ab7', '#a463f2', '#97bbf5', '#9c6b4e', '#9498a0',
        // Tableau (8 — dropped #59a14f, #9c755f as too close to Observable)
        '#4e79a7', '#f28e2c', '#e15759', '#76b7b2',
        '#edc949', '#af7aa1', '#ff9da7', '#bab0ab',
        // D3 Paired (8 — dropped #1f78b4, #fb9a99)
        '#a6cee3', '#b2df8a', '#33a02c', '#e31a1c',
        '#fdbf6f', '#ff7f00', '#cab2d6', '#6a3d9a',
        // D3 Set3 (7 — dropped #8dd3c7, #bebada, #fdb462)
        '#fb8072', '#80b1d3', '#b3de69', '#fccde5',
        '#bc80bd', '#ccebc5', '#ffed6f'
    ];
    window.Apex = window.Apex || {};
    window.Apex.colors = palette;
})();
