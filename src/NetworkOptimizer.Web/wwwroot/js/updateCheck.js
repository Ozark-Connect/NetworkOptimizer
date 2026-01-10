// Client-side update checker - fetches from GitHub API directly
// Caches results in localStorage, only checks every 15 minutes

window.updateChecker = {
    CACHE_KEY: 'networkOptimizer_updateCheck',
    CACHE_DURATION_MS: 15 * 60 * 1000, // 15 minutes
    GITHUB_API_URL: 'https://api.github.com/repos/Ozark-Connect/NetworkOptimizer/releases/latest',

    async checkForUpdate(currentVersion) {
        try {
            // Check cache first
            const cached = this.getCached();
            if (cached) {
                return this.compareVersions(currentVersion, cached.latestVersion);
            }

            // Fetch from GitHub
            const response = await fetch(this.GITHUB_API_URL, {
                headers: { 'Accept': 'application/vnd.github.v3+json' }
            });

            if (!response.ok) {
                console.warn('Update check failed:', response.status);
                return null;
            }

            const data = await response.json();
            const latestVersion = data.tag_name?.replace(/^v/, '') || null;
            const releaseUrl = data.html_url || null;

            // Cache the result
            this.setCache(latestVersion, releaseUrl);

            return this.compareVersions(currentVersion, latestVersion, releaseUrl);
        } catch (error) {
            console.warn('Update check error:', error);
            return null;
        }
    },

    getCached() {
        try {
            const cached = localStorage.getItem(this.CACHE_KEY);
            if (!cached) return null;

            const { timestamp, latestVersion, releaseUrl } = JSON.parse(cached);
            const age = Date.now() - timestamp;

            if (age < this.CACHE_DURATION_MS) {
                return { latestVersion, releaseUrl };
            }

            // Cache expired
            localStorage.removeItem(this.CACHE_KEY);
            return null;
        } catch {
            return null;
        }
    },

    setCache(latestVersion, releaseUrl) {
        try {
            localStorage.setItem(this.CACHE_KEY, JSON.stringify({
                timestamp: Date.now(),
                latestVersion,
                releaseUrl
            }));
        } catch {
            // localStorage might be full or disabled
        }
    },

    compareVersions(current, latest, releaseUrl) {
        if (!current || !latest) return null;

        // Normalize versions (remove 'v' prefix, handle source builds)
        const currentClean = current.replace(/^v/, '').split('+')[0];
        const latestClean = latest.replace(/^v/, '');

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
    }
};
