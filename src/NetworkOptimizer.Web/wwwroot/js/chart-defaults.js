// Grafana "Classic" palette — 24 visually distinct colors for multi-series charts.
// Source of truth for both Blazor-rendered ApexCharts (via window.Apex.colors)
// and JS module charts (via window.Apex.colors or direct import).
// Charts that set explicit Colors/colors override this default.
(function () {
    var palette = [
        '#7EB26D', '#EAB839', '#6ED0E0', '#EF843C', '#E24D42', '#1F78C1',
        '#BA43A9', '#705DA0', '#508642', '#CCA300', '#447EBC', '#C15C17',
        '#890F02', '#0A437C', '#6D1F62', '#584477', '#B7DBAB', '#F4D598',
        '#70DBED', '#F9BA8F', '#F29191', '#82B5D8', '#E5A8E2', '#AEA2E0'
    ];
    window.Apex = window.Apex || {};
    window.Apex.colors = palette;
})();
