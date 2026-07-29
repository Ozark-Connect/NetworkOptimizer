// The identity-provider buttons on the login page are links sitting outside the sign-in form, so the
// Keep me signed in checkbox is never submitted with them. Put the answer on the challenge URL at
// click time; the server carries it across the round trip to the provider.
//
// Delegated from document rather than bound per-link, and loaded as a real script rather than inlined
// in the page: the buttons are rendered further down the component than any script beside them, and
// enhanced navigation patches them in without re-running inline script. Both would leave the handler
// attached to nothing.
(function () {
    // Keep the rendered href honest as the box is ticked. Rewriting only at click time would leave a
    // middle-click, a ctrl-click or a copied link address carrying rememberMe=true from the markup -
    // none of those reach a click handler - and would show an address on hover that is not the one
    // being requested.
    function syncHrefs() {
        var box = document.querySelector('input[name="rememberMe"]');
        if (!box || box.type !== 'checkbox') return;

        var links = document.querySelectorAll('a[data-federation]');
        for (var i = 0; i < links.length; i++) {
            var url = new URL(links[i].getAttribute('href'), window.location.origin);
            url.searchParams.set('rememberMe', box.checked ? 'true' : 'false');
            links[i].setAttribute('href', url.pathname + url.search);
        }
    }

    document.addEventListener('change', function (e) {
        if (e.target && e.target.name === 'rememberMe') syncHrefs();
    });

    // Also on arrival: a reload restores the checkbox to how the user left it without firing change.
    syncHrefs();
    document.addEventListener('DOMContentLoaded', syncHrefs);
    if (window.Blazor) {
        Blazor.addEventListener('enhancedload', syncHrefs);
    }
    else {
        document.addEventListener('enhancedload', syncHrefs);
    }

    document.addEventListener('click', function (e) {
        var link = e.target.closest ? e.target.closest('a[data-federation]') : null;
        if (!link) return;

        var href = link.getAttribute('href');
        if (!href) return;

        e.preventDefault();
        var box = document.querySelector('input[name="rememberMe"]');
        var url = new URL(href, window.location.origin);
        url.searchParams.set('rememberMe', box && box.checked ? 'true' : 'false');
        window.location.href = url.toString();
    });
})();
