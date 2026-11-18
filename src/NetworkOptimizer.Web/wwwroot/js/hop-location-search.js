// City search for setting a trace hop's location. Geocodes with OpenStreetMap Nominatim, as the
// maps' address search does: only on submit (Enter), never per keystroke, per its usage policy.

const GEOCODE_URL = 'https://nominatim.openstreetmap.org/search';

let placed = null;

/**
 * Pins the editor below its button with fixed positioning, so the table's scroll box does not clip
 * it, and keeps it there on scroll and resize. A phone's bottom sheet (app.css) is left alone.
 * @param {HTMLElement} pop
 * @param {HTMLElement} anchor
 */
export function place(pop, anchor) {
    release();
    if (!pop || !anchor) return;
    const update = () => {
        if (window.matchMedia('(max-width: 768px)').matches) {
            pop.style.position = pop.style.top = pop.style.left = '';
            return;
        }
        const r = anchor.getBoundingClientRect();
        const width = pop.offsetWidth;
        pop.style.position = 'fixed';
        pop.style.top = `${r.bottom + 8}px`;
        pop.style.left = `${Math.max(16, Math.min(r.right - width + 40, window.innerWidth - width - 16))}px`;
    };
    update();
    window.addEventListener('scroll', update, true);
    window.addEventListener('resize', update);
    placed = update;
}

/** Stops following the button: call when the editor closes. */
export function release() {
    if (!placed) return;
    window.removeEventListener('scroll', placed, true);
    window.removeEventListener('resize', placed);
    placed = null;
}

/**
 * Cities, towns, and villages matching a query.
 * @param {string} query
 * @returns {Promise<Array<{city: string, region: string|null, country: string|null, countryCode: string, latitude: number, longitude: number}>>}
 *   Empty when nothing matches or the service cannot be reached.
 */
export async function search(query) {
    const q = (query || '').trim();
    if (!q) return [];
    const url = `${GEOCODE_URL}?format=jsonv2&addressdetails=1&featureType=settlement&limit=8&q=${encodeURIComponent(q)}`;
    let list;
    try {
        const r = await fetch(url, { headers: { 'Accept': 'application/json' } });
        list = r.ok ? await r.json() : [];
    } catch (e) {
        console.warn('Hop location search failed', e);
        return [];
    }
    const seen = new Set();
    const out = [];
    for (const hit of list || []) {
        const a = hit.address || {};
        const city = hit.name || a.city || a.town || a.village || a.municipality || a.hamlet;
        const code = (a.country_code || '').toUpperCase();
        const lat = parseFloat(hit.lat), lon = parseFloat(hit.lon);
        if (!city || code.length !== 2 || isNaN(lat) || isNaN(lon)) continue;
        const region = a.state || a.province || a.region || a.county || null;
        const key = `${city}|${region}|${code}`;
        if (seen.has(key)) continue;
        seen.add(key);
        out.push({ city, region, country: a.country || null, countryCode: code, latitude: lat, longitude: lon });
    }
    return out;
}
