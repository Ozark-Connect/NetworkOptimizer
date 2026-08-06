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

// Event annotation colors, by the severity the server assigns each mark. Muted is deliberate
// for the info tier: a planned restart should read as background, not as something to chase.
export const eventColor = (severity) =>
    severity === 'critical' ? resolve('--danger-color', '#ee6368')
        : severity === 'warning' ? resolve('--warning-color', '#e79613')
            : resolve('--text-muted', '#5c5c66');

export const chartSurfaceColor = () => resolve('--bg-tertiary', '#232326');
