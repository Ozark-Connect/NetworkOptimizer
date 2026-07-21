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

    // Passwordless (usernameless) login with a passkey. Reloads on success.
    async function login() {
        if (!window.isSecureContext || !window.PublicKeyCredential) return false;

        const optionsRes = await fetch('/api/passkey/request-options', { credentials: 'same-origin' });
        if (!optionsRes.ok) return false;
        const optionsJson = await optionsRes.text();

        const options = PublicKeyCredential.parseRequestOptionsFromJSON(JSON.parse(optionsJson).publicKey
            ?? JSON.parse(optionsJson));
        const assertion = await navigator.credentials.get({ publicKey: options });
        if (!assertion) return false;

        const assertRes = await fetch('/api/passkey/assert', {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            credentials: 'same-origin',
            body: JSON.stringify(assertion.toJSON()),
        });
        if (assertRes.ok) {
            window.location.href = '/';
            return true;
        }
        return false;
    }

    return { register, login };
})();
