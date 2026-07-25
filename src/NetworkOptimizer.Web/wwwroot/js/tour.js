// Guided tour driver: dims the page, spotlights a [data-tour] target, and shows a
// step card with Back / Next / Skip. Navigation is done by Blazor (deep links only);
// this script only waits for the target and renders the overlay.
window.noTour = (function () {
    'use strict';

    let active = null; // { dotNetRef, overlay, hole, card, target, cleanup: [] }
    let modalEsc = null;

    function waitFor(selector, timeoutMs) {
        return new Promise(resolve => {
            const started = Date.now();
            const tick = () => {
                if (!document.body) return resolve(null);
                // First visible match: the same anchor can exist on hidden siblings
                // (e.g. the 3D and 2D map variants of the Live View timeline).
                const el = Array.from(document.querySelectorAll(selector))
                    .find(e => e.offsetParent !== null && e.getBoundingClientRect().width > 0);
                if (el) return resolve(el);
                if (Date.now() - started > timeoutMs) return resolve(null);
                setTimeout(tick, 150);
            };
            tick();
        });
    }

    function teardown() {
        if (!active) return;
        active.cleanup.forEach(fn => { try { fn(); } catch { } });
        if (active.overlay) active.overlay.remove();
        active = null;
    }

    function invoke(action) {
        if (!active || !active.dotNetRef) return;
        try { active.dotNetRef.invokeMethodAsync('TourAdvance', action); } catch { }
    }

    function position() {
        if (!active || !active.target || !document.contains(active.target)) return;
        const pad = 6;
        const r = active.target.getBoundingClientRect();
        const hole = active.hole;
        hole.style.left = (r.left - pad) + 'px';
        hole.style.top = (r.top - pad) + 'px';
        hole.style.width = (r.width + pad * 2) + 'px';
        hole.style.height = (r.height + pad * 2) + 'px';

        const card = active.card;
        const cw = card.offsetWidth, ch = card.offsetHeight;
        const gap = 12, m = 10;
        const vw = window.innerWidth, vh = window.innerHeight;
        let placement = active.placement || 'auto';
        if (placement === 'auto') {
            placement = r.bottom + gap + ch < vh ? 'bottom'
                : r.top - gap - ch > 0 ? 'top'
                : r.right + gap + cw < vw ? 'right' : 'left';
        }
        let left, top;
        switch (placement) {
            case 'top': left = r.left + r.width / 2 - cw / 2; top = r.top - gap - ch; break;
            case 'left': left = r.left - gap - cw; top = r.top + r.height / 2 - ch / 2; break;
            case 'right': left = r.right + gap; top = r.top + r.height / 2 - ch / 2; break;
            default: left = r.left + r.width / 2 - cw / 2; top = r.bottom + gap; break;
        }
        card.style.left = Math.max(m, Math.min(left, vw - cw - m)) + 'px';
        card.style.top = Math.max(m, Math.min(top, vh - ch - m)) + 'px';
    }

    // Minimal inline markup for step copy: **bold** only. Built as DOM nodes so tour
    // JSON can never inject HTML.
    function renderInline(el, text) {
        String(text || '').split(/\*\*([^*]+)\*\*/g).forEach((part, i) => {
            if (!part) return;
            if (i % 2) {
                const b = document.createElement('strong');
                b.textContent = part;
                el.appendChild(b);
            } else {
                el.appendChild(document.createTextNode(part));
            }
        });
    }

    function buildCard(opts) {
        const card = document.createElement('div');
        card.className = 'tour-card';

        const progress = document.createElement('div');
        progress.className = 'tour-card-progress';
        const counter = document.createElement('span');
        counter.textContent = 'Step ' + (opts.index + 1) + ' of ' + opts.total;
        progress.appendChild(counter);
        const badge = document.createElement('span');
        badge.className = 'tour-card-badge tour-card-badge-' + (opts.badge === 'improved' ? 'improved' : 'new');
        badge.textContent = opts.badge === 'improved' ? 'Improved' : 'New';
        progress.appendChild(badge);
        card.appendChild(progress);

        const title = document.createElement('div');
        title.className = 'tour-card-title';
        title.textContent = opts.title;
        card.appendChild(title);

        const body = document.createElement('div');
        body.className = 'tour-card-body';
        renderInline(body, opts.body);
        card.appendChild(body);

        const actions = document.createElement('div');
        actions.className = 'tour-card-actions';

        const skip = document.createElement('button');
        skip.type = 'button';
        skip.className = 'tour-card-skip';
        skip.textContent = 'Skip tour';
        skip.addEventListener('click', () => invoke('skip'));
        actions.appendChild(skip);

        const spacer = document.createElement('div');
        spacer.className = 'tour-card-spacer';
        actions.appendChild(spacer);

        if (opts.hasBack) {
            const back = document.createElement('button');
            back.type = 'button';
            back.className = 'btn btn-secondary btn-sm';
            back.textContent = 'Back';
            back.addEventListener('click', () => invoke('back'));
            actions.appendChild(back);
        }

        const next = document.createElement('button');
        next.type = 'button';
        next.className = 'btn btn-primary btn-sm';
        next.textContent = opts.index + 1 >= opts.total ? 'Done' : 'Next';
        next.addEventListener('click', () => invoke('next'));
        actions.appendChild(next);

        card.appendChild(actions);
        return { card, next };
    }

    return {
        // Returns 'shown' when the target was found and spotlighted, 'missing' otherwise.
        showStep: async function (dotNetRef, opts) {
            teardown();
            const el = await waitFor(opts.selector, opts.waitMs || 8000);
            if (!el) return 'missing';

            el.scrollIntoView({ block: 'center', behavior: 'instant' });

            const overlay = document.createElement('div');
            overlay.className = 'tour-overlay';

            const hole = document.createElement('div');
            hole.className = 'tour-hole';
            overlay.appendChild(hole);

            const { card, next } = buildCard(opts);
            overlay.appendChild(card);
            document.body.appendChild(overlay);

            active = { dotNetRef, overlay, hole, card, target: el, placement: opts.placement, cleanup: [] };

            const onKey = e => {
                if (e.key === 'Escape') { e.stopPropagation(); invoke('escape'); }
            };
            document.addEventListener('keydown', onKey, true);
            active.cleanup.push(() => document.removeEventListener('keydown', onKey, true));

            const onMove = () => position();
            window.addEventListener('resize', onMove);
            window.addEventListener('scroll', onMove, true);
            active.cleanup.push(() => {
                window.removeEventListener('resize', onMove);
                window.removeEventListener('scroll', onMove, true);
            });
            // Charts and cards keep loading after first paint; track layout drift cheaply.
            const drift = setInterval(position, 500);
            active.cleanup.push(() => clearInterval(drift));

            position();
            requestAnimationFrame(position);
            try { next.focus(); } catch { }
            return 'shown';
        },

        end: function () {
            teardown();
        },

        // Escape-to-dismiss for the Blazor offer modal.
        modalEscape: function (dotNetRef, enable) {
            if (modalEsc) { document.removeEventListener('keydown', modalEsc, true); modalEsc = null; }
            if (enable && dotNetRef) {
                modalEsc = e => {
                    if (e.key === 'Escape') {
                        e.stopPropagation();
                        try { dotNetRef.invokeMethodAsync('TourModalEscape'); } catch { }
                    }
                };
                document.addEventListener('keydown', modalEsc, true);
            }
        }
    };
})();
