// Guided tour driver: dims the page, spotlights a [data-tour] target, and shows a
// step card with Back / Next / Skip. Navigation is done by Blazor (deep links only);
// this script only waits for the target and renders the overlay.
window.noTour = (function () {
    'use strict';

    let active = null; // { dotNetRef, overlay, hole, card, target, cleanup: [] }
    let modalEsc = null;
    // Monotonic token: a showStep whose waitFor outlives a newer showStep or an end()
    // must not build a zombie overlay for a tour that already moved on.
    let generation = 0;

    // A collapsed section clips its content rather than removing it: the wrapper is a zero-height
    // grid row and the content overflows hidden, so a target inside still reports a normal
    // offsetParent and a normal height. Measuring the target alone cannot tell collapsed from
    // visible - the answer is in its ancestors.
    function collapsedAncestors(el) {
        const found = [];
        for (let node = el.parentElement; node && node !== document.body; node = node.parentElement) {
            if (node.classList.contains('expand-wrapper') && !node.classList.contains('expanded')) found.push(node);
        }
        return found;
    }

    // Open any collapsed section the target sits in, by clicking the header that owns it so the
    // component's own state flips (setting the class by hand would be undone on its next render).
    // Each wrapper is opened at most once per step: a section the user closes deliberately during
    // the tour stays closed. Returns whether anything was opened.
    function revealCollapsedAncestors(el, opened) {
        let clicked = false;
        for (const node of collapsedAncestors(el)) {
            if (opened.has(node)) continue;
            opened.add(node);
            const toggle = node.previousElementSibling;
            if (toggle && toggle.classList.contains('card-header-collapsible')) {
                toggle.click();
                clicked = true;
            } else {
                console.warn('noTour: target sits in a collapsed section with no recognized toggle', node);
            }
        }
        return clicked;
    }

    // Narrows a spotlight from a list to the row that actually mentions something. An anchor can
    // only be placed on markup that always exists, but the interesting target is often one row
    // among many, and which rows exist depends on the user's own configuration. Falls back to the
    // anchor whenever the text is absent, so a step never fails over wording it hoped to find.
    function narrowToText(anchor, text) {
        if (!anchor || !text) return anchor;
        const needle = text.toLowerCase();
        const hits = Array.from(anchor.querySelectorAll('*')).filter(e =>
            e.offsetParent !== null && (e.textContent || '').toLowerCase().includes(needle));
        if (!hits.length) return anchor;

        // Ancestors precede their descendants in document order, so the hits that contain no
        // other hit are the words themselves; the first of those is the earliest match on the
        // page. Spotlighting the words alone reads too tightly, so climb to the row holding
        // them, stopping at the anchor.
        const innermost = hits.find(e => !hits.some(o => o !== e && e.contains(o))) || hits[0];
        let row = innermost;
        while (row && row !== anchor) {
            const tag = row.tagName.toLowerCase();
            const cls = (row.className || '').toString().toLowerCase();
            if (tag === 'tr' || tag === 'li' || cls.includes('row') || cls.includes('item')) return row;
            row = row.parentElement;
        }
        return innermost.parentElement && innermost.parentElement !== anchor
            ? innermost.parentElement
            : innermost;
    }

    function waitFor(selector, timeoutMs, opened) {
        return new Promise(resolve => {
            const started = Date.now();
            let settleUntil = 0;
            const tick = () => {
                if (!document.body) return resolve(null);
                const matches = Array.from(document.querySelectorAll(selector));
                // Open before measuring, not after: a clipped target passes every measurement a
                // visible one does, so a filter-first pass would accept it and never get here.
                if (matches.map(e => revealCollapsedAncestors(e, opened)).some(Boolean)) {
                    settleUntil = Date.now() + 350; // let the expand transition finish before measuring
                }
                if (Date.now() - started > timeoutMs) return resolve(null);
                if (Date.now() < settleUntil) return setTimeout(tick, 100);
                // The same anchor can exist more than once (the 3D and 2D Live View maps
                // both carry the timeline anchor). Pick by LAYOUT order, not DOM order:
                // the user can swap the two maps, and the spotlight should follow
                // whichever is actually on top.
                const visible = matches.filter(e => e.offsetParent !== null
                    && e.getBoundingClientRect().width > 0 && e.getBoundingClientRect().height > 0
                    && collapsedAncestors(e).length === 0);
                if (visible.length) {
                    visible.sort((a, b) => a.getBoundingClientRect().top - b.getBoundingClientRect().top);
                    return resolve(visible[0]);
                }
                setTimeout(tick, 150);
            };
            tick();
        });
    }

    // The app scrolls .main-content (mobile) or .page-content (desktop) rather than the
    // document, and scrollIntoView does not always settle those before we measure. Scroll,
    // verify, then move the owning scroller by hand if the target is still off screen.
    function scrollableAncestor(el) {
        let node = el.parentElement;
        while (node && node !== document.body) {
            const oy = getComputedStyle(node).overflowY;
            if ((oy === 'auto' || oy === 'scroll') && node.scrollHeight > node.clientHeight + 4) return node;
            node = node.parentElement;
        }
        return document.scrollingElement || document.documentElement;
    }

    function sleep(ms) {
        return new Promise(r => setTimeout(r, ms));
    }

    async function ensureInView(el) {
        try {
            el.scrollIntoView({ block: 'center', inline: 'nearest' });
        } catch {
            el.scrollIntoView();
        }
        await sleep(200);

        const rect = el.getBoundingClientRect();
        const vh = window.innerHeight;
        if (rect.top >= 8 && rect.bottom <= vh - 8) return;

        const scroller = scrollableAncestor(el);
        const isDocument = scroller === document.scrollingElement || scroller === document.documentElement;
        const viewTop = isDocument ? 0 : scroller.getBoundingClientRect().top;
        const viewHeight = isDocument ? vh : scroller.clientHeight;
        scroller.scrollTop += (rect.top - viewTop) - (viewHeight / 2) + (rect.height / 2);
        await sleep(150);
    }

    function teardown() {
        if (!active) return;
        active.cleanup.forEach(fn => { try { fn(); } catch { } });
        const overlay = active.overlay;
        if (overlay) {
            // Fade the dim and card out, then remove; a replacement overlay from the
            // next step cross-fades over this one.
            overlay.classList.remove('tour-overlay-on');
            setTimeout(() => overlay.remove(), 300);
        }
        active = null;
    }

    function invoke(action) {
        if (!active || !active.dotNetRef) return;
        try { active.dotNetRef.invokeMethodAsync('TourAdvance', action); } catch { }
    }

    function position() {
        if (!active || !active.target || !document.contains(active.target)) return;
        // A page that finishes loading after the spotlight lands can collapse the section out from
        // under it - Adaptive SQM opens its WAN cards, then closes the ones that turn out to have a
        // saved config. Reopen on the drift tick; the once-per-step rule keeps a section the user
        // closed on purpose from springing back.
        revealCollapsedAncestors(active.target, active.opened);
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
        badge.className = 'tour-badge tour-badge-' + (opts.badge === 'improved' ? 'improved' : 'new');
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

        // No Skip on the last step: there is nothing left to skip, and taking it there would file
        // a tour the user actually finished as deferred. Done is the only sensible exit.
        const isLast = opts.index + 1 >= opts.total;
        if (!isLast) {
            const skip = document.createElement('button');
            skip.type = 'button';
            skip.className = 'tour-card-skip';
            skip.textContent = 'Skip tour';
            skip.addEventListener('click', () => invoke('skip'));
            actions.appendChild(skip);
        }

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
        next.textContent = isLast ? 'Done' : 'Next';
        next.addEventListener('click', () => invoke('next'));
        actions.appendChild(next);

        card.appendChild(actions);
        return { card, next };
    }

    return {
        // Returns 'shown' when the target was found and spotlighted, 'missing' when it
        // never appeared, 'stale' when superseded mid-wait.
        showStep: async function (dotNetRef, opts) {
            const gen = ++generation;
            teardown();
            // Shared with position() so a section opened during the wait is not reopened after,
            // and so the once-per-step rule spans the whole step rather than just the wait.
            const opened = new Set();
            const anchor = await waitFor(opts.selector, opts.waitMs || 8000, opened);
            if (gen !== generation) return 'stale';
            if (!anchor) return 'missing';

            const el = narrowToText(anchor, opts.matchText);

            await ensureInView(el);
            if (gen !== generation) return 'stale';

            const overlay = document.createElement('div');
            overlay.className = 'tour-overlay';

            const hole = document.createElement('div');
            hole.className = 'tour-hole';
            overlay.appendChild(hole);

            const { card, next } = buildCard(opts);
            overlay.appendChild(card);
            document.body.appendChild(overlay);

            active = { dotNetRef, overlay, hole, card, target: el, placement: opts.placement, opened, cleanup: [] };

            const onKey = e => {
                if (e.key === 'Escape') { e.stopPropagation(); invoke('escape'); }
            };
            document.addEventListener('keydown', onKey, true);
            active.cleanup.push(() => document.removeEventListener('keydown', onKey, true));

            // Interacting with the demo'd element lifts the dim for the rest of the step
            // so whatever it reveals is seen in full. One-shot on purpose: restoring on
            // mouseleave made scrolling flicker the dim on and off. A press lifts
            // immediately; a hover must dwell briefly so brushing past the target does
            // not break the spotlight.
            let dwell = null;
            const clearDim = () => overlay.classList.add('tour-overlay-clear');
            const onEnter = () => { if (!dwell) dwell = setTimeout(clearDim, 250); };
            const onLeave = () => { clearTimeout(dwell); dwell = null; };
            el.addEventListener('mouseenter', onEnter);
            el.addEventListener('mouseleave', onLeave);
            el.addEventListener('pointerdown', clearDim);
            active.cleanup.push(() => {
                clearTimeout(dwell);
                el.removeEventListener('mouseenter', onEnter);
                el.removeEventListener('mouseleave', onLeave);
                el.removeEventListener('pointerdown', clearDim);
            });

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
            requestAnimationFrame(() => {
                position();
                overlay.classList.add('tour-overlay-on');
            });
            try { next.focus(); } catch { }
            return 'shown';
        },

        end: function () {
            ++generation;
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
