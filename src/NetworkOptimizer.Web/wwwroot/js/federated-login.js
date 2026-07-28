// The identity-provider buttons on the login page are links sitting outside the sign-in form, so the
// Keep me signed in checkbox is never submitted with them. Put the answer on the challenge URL at
// click time; the server carries it across the round trip to the provider.
//
// Delegated from document rather than bound per-link, and loaded as a real script rather than inlined
// in the page: the buttons are rendered further down the component than any script beside them, and
// enhanced navigation patches them in without re-running inline script. Both would leave the handler
// attached to nothing.
(function () {
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
