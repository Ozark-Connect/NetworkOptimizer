// Where It's Going draws our app marks through SVG masks: an SVG <image> cannot take its href from CSS, so each
// mask's image reads the --no-icon data URL its class sets in app.css, which stays the one source of the marks.
export function fill(root) {
    if (!root) return;
    for (const img of root.querySelectorAll('image.lt-sankey-mark-src')) {
        const m = /^url\(["']?(.*?)["']?\)$/.exec(getComputedStyle(img).getPropertyValue('--no-icon').trim());
        if (m && img.getAttribute('href') !== m[1]) img.setAttribute('href', m[1]);
    }
}
