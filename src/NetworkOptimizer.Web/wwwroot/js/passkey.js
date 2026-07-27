// WebAuthn passkey ceremonies. The server (.NET 10 Identity) produces the options JSON and validates
// the browser's response; these helpers just bridge to navigator.credentials and POST the result back.
// Requires a secure context (HTTPS or localhost) - callers gate on window.isSecureContext.
window.netoptPasskey = (function () {
    async function postJson(url, body) {
        const res = await fetch(url, {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            credentials: 'same-origin',
            body: body,
        });
        return res;
    }

    // Register a new passkey for the signed-in user. Returns true on success.
    async function register(name) {
        if (!window.isSecureContext || !window.PublicKeyCredential) return false;

        const optionsRes = await postJson('/api/passkey/creation-options', null);
        if (!optionsRes.ok) return false;
        const optionsJson = await optionsRes.text();

        const options = PublicKeyCredential.parseCreationOptionsFromJSON(JSON.parse(optionsJson).publicKey
            ?? JSON.parse(optionsJson));
        const credential = await navigator.credentials.create({ publicKey: options });
        if (!credential) return false;

        const registerRes = await postJson('/api/passkey/register',
            JSON.stringify({ credential: JSON.stringify(credential.toJSON()), name: name || null }));
        return registerRes.ok;
    }

    // Sends the user back to the login page with a reason, keeping the tab's site pin.
    function failLogin(reason) {
        const site = new URLSearchParams(window.location.search).get('site');
        const query = site ? `?error=${reason}&site=${encodeURIComponent(site)}` : `?error=${reason}`;
        window.location.href = '/login' + query;
        return false;
    }

    // Passwordless (usernameless) login with a passkey. Reloads on success; every other outcome
    // says why, because a button that silently does nothing reads as a broken page - and the most
    // likely cause is a credential that was removed from the account server-side.
    async function login() {
        if (!window.isSecureContext || !window.PublicKeyCredential) return failLogin('passkey_insecure');

        let assertion;
        try {
            const optionsRes = await fetch('/api/passkey/request-options', { credentials: 'same-origin' });
            if (!optionsRes.ok) return failLogin('passkey_failed');
            const optionsJson = await optionsRes.text();

            const options = PublicKeyCredential.parseRequestOptionsFromJSON(JSON.parse(optionsJson).publicKey
                ?? JSON.parse(optionsJson));
            assertion = await navigator.credentials.get({ publicKey: options });
        } catch (err) {
            // Dismissing the browser prompt is a choice, not a failure: leave the page alone.
            if (err && (err.name === 'NotAllowedError' || err.name === 'AbortError')) return false;
            return failLogin('passkey_failed');
        }

        if (!assertion) return false;

        const remember = document.querySelector('input[name="rememberMe"]')?.checked ? 'true' : 'false';
        const assertRes = await fetch(`/api/passkey/assert?rememberMe=${remember}`, {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            credentials: 'same-origin',
            body: JSON.stringify(assertion.toJSON()),
        });
        if (assertRes.ok) {
            window.location.href = '/';
            return true;
        }

        // The ceremony worked but the server would not accept it - the usual cause is a passkey that
        // no longer exists on the account.
        return failLogin('passkey_rejected');
    }

    return { register, login };
})();
