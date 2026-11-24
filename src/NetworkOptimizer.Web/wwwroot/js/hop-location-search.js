// City search for setting a trace hop's location. Geocodes with OpenStreetMap Nominatim, as the
// maps' address search does: only on submit (Enter), never per keystroke, per its usage policy.

const GEOCODE_URL = 'https://nominatim.openstreetmap.org/search';

// The fixed placement is shared with the device restart sheet.
export { place, release } from './fixed-popover.js?v=2';

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
