// Download / upload colors for charts, read from the same CSS custom properties
// the speed test results use so charts and stat cards never drift apart.
// ApexCharts writes colors into SVG presentation attributes, where var() does not
// resolve, so they are resolved to concrete values here.

function resolve(name, fallback) {
    const value = getComputedStyle(document.documentElement).getPropertyValue(name).trim();
    return /^(#|rgb|hsl)/i.test(value) ? value : fallback;
}

export const downloadColor = () => resolve('--speed-download-color', '#2E79C4');
export const uploadColor = () => resolve('--speed-upload-color', '#24bc70');
