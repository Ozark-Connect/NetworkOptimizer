export async function downloadSupportFile(url) {
    const resp = await fetch(url, { credentials: 'same-origin' });
    if (!resp.ok) {
        const body = await resp.json().catch(() => null);
        throw new Error(body?.error || 'Server returned ' + resp.status);
    }

    const disposition = resp.headers.get('content-disposition') || '';
    const match = disposition.match(/filename="?([^";\s]+)"?/);
    const filename = match ? match[1] : 'support-file.tgz';

    const blob = await resp.blob();
    const a = document.createElement('a');
    a.href = URL.createObjectURL(blob);
    a.download = filename;
    document.body.appendChild(a);
    a.click();
    document.body.removeChild(a);
    URL.revokeObjectURL(a.href);
}
