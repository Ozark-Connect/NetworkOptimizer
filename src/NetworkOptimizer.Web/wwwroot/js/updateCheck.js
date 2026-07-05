// Client-side update checker - fetches from GitHub API directly
// Caches results in localStorage, only checks every 15 minutes
//
// Two modes:
// - Stable (default): hits /releases/latest, which GitHub defines to EXCLUDE
//   prereleases, and compares numeric cores only (suffixes stripped).
// - Pre-release opt-in: hits the /releases list (which includes prereleases),
//   picks the highest by full semver, and compares suffix-aware so a tester on
//   2.0.0-beta.1 is nudged to 2.0.0-beta.2 / 2.0.0 but never "downgraded" to an
//   older stable. Off by default so the stable majority is untouched.

window.updateChecker = {
    CACHE_KEY: 'networkOptimizer_updateCheck',
    CACHE_DURATION_MS: 15 * 60 * 1000, // 15 minutes
    GITHUB_API_URL: 'https://api.github.com/repos/Ozark-Connect/NetworkOptimizer/releases/latest',
    GITHUB_RELEASES_URL: 'https://api.github.com/repos/Ozark-Connect/NetworkOptimizer/releases?per_page=30',

    async checkForUpdate(currentVersion, includePrereleases = false) {
        try {
            // Check cache first (keyed by mode so toggling the opt-in re-fetches)
            const cached = this.getCached(includePrereleases);
            if (cached) {
                return includePrereleases
                    ? this.compareVersionsPrecise(currentVersion, cached.latestVersion, cached.releaseUrl)
                    : this.compareVersions(currentVersion, cached.latestVersion, cached.releaseUrl);
            }

            // Fetch from GitHub
            const url = includePrereleases ? this.GITHUB_RELEASES_URL : this.GITHUB_API_URL;
            const response = await fetch(url, {
                headers: { 'Accept': 'application/vnd.github.v3+json' }
            });

            if (!response.ok) {
                console.warn('Update check failed:', response.status);
                return null;
            }

            const data = await response.json();

            let latestVersion, releaseUrl;
            if (includePrereleases) {
                // The list includes prereleases (but not drafts, for anonymous callers).
                // Pick the highest by semver rather than trusting list order.
                let best = null;
                for (const r of (Array.isArray(data) ? data : [])) {
                    if (r.draft) continue;
                    const v = r.tag_name?.replace(/^v/, '');
                    if (!v) continue;
                    if (!best || this.compareSemver(v, best.version) > 0) {
                        best = { version: v, url: r.html_url };
                    }
                }
                latestVersion = best?.version || null;
                releaseUrl = best?.url || null;
            } else {
                latestVersion = data.tag_name?.replace(/^v/, '') || null;
                releaseUrl = data.html_url || null;
            }

            // Cache the result
            this.setCache(latestVersion, releaseUrl, includePrereleases);

            return includePrereleases
                ? this.compareVersionsPrecise(currentVersion, latestVersion, releaseUrl)
                : this.compareVersions(currentVersion, latestVersion, releaseUrl);
        } catch (error) {
            console.warn('Update check error:', error);
            return null;
        }
    },

    getCached(includePrereleases = false) {
        try {
            const cached = localStorage.getItem(this.CACHE_KEY);
            if (!cached) return null;

            const { timestamp, latestVersion, releaseUrl, prereleases } = JSON.parse(cached);
            const age = Date.now() - timestamp;

            // A cache entry is only valid for the mode it was fetched in.
            if (age < this.CACHE_DURATION_MS && !!prereleases === !!includePrereleases) {
                return { latestVersion, releaseUrl };
            }

            // Cache expired or from the other mode
            localStorage.removeItem(this.CACHE_KEY);
            return null;
        } catch {
            return null;
        }
    },

    setCache(latestVersion, releaseUrl, includePrereleases = false) {
        try {
            localStorage.setItem(this.CACHE_KEY, JSON.stringify({
                timestamp: Date.now(),
                latestVersion,
                releaseUrl,
                prereleases: !!includePrereleases
            }));
        } catch {
            // localStorage might be full or disabled
        }
    },

    compareVersions(current, latest, releaseUrl) {
        if (!current || !latest) return null;

        // Normalize versions:
        // - Remove 'v' prefix
        // - Remove build metadata (+sha)
        // - Remove pre-release suffix (-alpha.0.1) for comparison
        const currentClean = current.replace(/^v/, '').split('+')[0].split('-')[0];
        const latestClean = latest.replace(/^v/, '').split('-')[0];

        // Skip check for source builds
        if (currentClean.startsWith('0.0.0')) {
            return null;
        }

        const currentParts = currentClean.split('.').map(Number);
        const latestParts = latestClean.split('.').map(Number);

        for (let i = 0; i < Math.max(currentParts.length, latestParts.length); i++) {
            const c = currentParts[i] || 0;
            const l = latestParts[i] || 0;
            if (l > c) {
                return { updateAvailable: true, latestVersion: latest, releaseUrl };
            }
            if (c > l) {
                return { updateAvailable: false };
            }
        }

        return { updateAvailable: false };
    },

    // Suffix-aware comparison for the pre-release opt-in path: an update is only
    // offered when the candidate is strictly greater by full semver, so a beta
    // tester is never nudged "up" to an older stable release.
    compareVersionsPrecise(current, latest, releaseUrl) {
        if (!current || !latest) return null;

        // Skip check for source builds
        if (current.replace(/^v/, '').startsWith('0.0.0')) {
            return null;
        }

        if (this.compareSemver(latest, current) > 0) {
            return { updateAvailable: true, latestVersion: latest, releaseUrl };
        }
        return { updateAvailable: false };
    },

    // Parse into { core: [major, minor, patch], pre: [identifiers] }, dropping the
    // 'v' prefix and +build metadata. A missing pre-release means a final release.
    parseSemver(v) {
        const clean = v.replace(/^v/, '').split('+')[0];
        const dash = clean.indexOf('-');
        const core = (dash === -1 ? clean : clean.slice(0, dash)).split('.').map(Number);
        const pre = dash === -1 ? [] : clean.slice(dash + 1).split('.');
        return { core, pre };
    },

    // Full semver precedence: -1 / 0 / 1. Core compared numerically; a release
    // outranks a prerelease of the same core; prerelease identifiers per semver
    // (numeric < alphanumeric, more identifiers win when all preceding are equal).
    compareSemver(a, b) {
        const pa = this.parseSemver(a);
        const pb = this.parseSemver(b);

        for (let i = 0; i < 3; i++) {
            const d = (pa.core[i] || 0) - (pb.core[i] || 0);
            if (d !== 0) return d > 0 ? 1 : -1;
        }

        if (pa.pre.length === 0 && pb.pre.length === 0) return 0;
        if (pa.pre.length === 0) return 1;  // release > prerelease
        if (pb.pre.length === 0) return -1;

        const n = Math.max(pa.pre.length, pb.pre.length);
        for (let i = 0; i < n; i++) {
            if (i >= pa.pre.length) return -1; // fewer identifiers = lower
            if (i >= pb.pre.length) return 1;
            const x = pa.pre[i], y = pb.pre[i];
            const xn = /^\d+$/.test(x), yn = /^\d+$/.test(y);
            if (xn && yn) {
                const d = Number(x) - Number(y);
                if (d !== 0) return d > 0 ? 1 : -1;
            } else if (xn) {
                return -1; // numeric identifiers have lower precedence than alphanumeric
            } else if (yn) {
                return 1;
            } else if (x !== y) {
                return x < y ? -1 : 1;
            }
        }

        return 0;
    }
};
