// "On this page": an index of the cards on a long page, so a card under a long table is one click
// away. App-wide and automatic: every page with 2 or more titled cards, one of them starting below
// the first screen, gets it; a page opts out with data-no-card-index on any element, and a card
// with no .card-title joins with data-card-index="Name".
//
// Two forms, by the room there is. Beside the content, when its left gutter fits the list: a list
// that follows the scroll, the card in view highlighted. Otherwise a row of card names just above
// the first card. Blazor owns the page's DOM, so the list lives on <body>; the row is the one node
// this adds to a page, never inside a grid or side-by-side row, and is rebuilt whenever Blazor
// re-renders it away.
(function () {
    'use strict';

    var MIN_CARDS = 2;
    var LIST_WIDTH = 168;   // the narrowest list, matching .card-index width in app.css
    var LIST_MAX = 260;     // a wider gutter widens the list up to this, so names wrap less
    var LINE = 48;          // the reading line, in px below the pane's top
    var GAP = 12;          // between the list and the content, and the list and the gutter's far edge
    var list = null, row = null, cards = [], signature = '', pending = 0, scroller = null;
    // The card a click jumped to stays highlighted until the reader scrolls: one near the end
    // cannot reach the top, and the page's bottom would otherwise mark the last card.
    var pinned = -1;
    // The card lit last and the scroll position then, so a scroll moves the highlight only its way.
    var shown = -1, lastTop = 0;
    // Where a scroll down began stepping through the rows left near the end (see place).
    var stepFrom = null;

    function content() { return document.querySelector('article.content'); }

    function scrollerOf(node) {
        for (var n = node.parentElement; n && n !== document.body; n = n.parentElement) {
            var oy = getComputedStyle(n).overflowY;
            if ((oy === 'auto' || oy === 'scroll') && n.scrollHeight > n.clientHeight + 4) return n;
        }
        return document.scrollingElement || document.documentElement;
    }

    function paneRect(sc) {
        var isDoc = sc === document.scrollingElement || sc === document.documentElement;
        return isDoc ? { top: 0, bottom: window.innerHeight, left: 0 } : sc.getBoundingClientRect();
    }

    // A card's name: its title without the grey suffix ("Snapshots", not "Snapshots 170 kept, 5 MB")
    // or any control in it (Access Layer's technology select would add every option's text).
    // A card without a .card-title names itself with data-card-index="Name".
    function nameOf(card) {
        var given = card.getAttribute('data-card-index');
        if (given) return given;
        var title = card.querySelector('.card-header .card-title');
        if (!title || title.closest('.card') !== card) return '';
        var copy = title.cloneNode(true);
        Array.prototype.forEach.call(copy.querySelectorAll('.card-title-suffix, select, button, input, textarea'),
            function (n) { n.remove(); });
        return copy.textContent.replace(/\s+/g, ' ').trim();
    }

    // In reading order, not DOM order: top to bottom, then left to right along a shared top. A grid
    // that places cards itself (the Dashboard) puts its second column's first card after the first
    // column's in the DOM.
    function findCards(root) {
        var found = Array.prototype.filter.call(root.querySelectorAll('.card'), function (c) {
            return c.offsetParent !== null && !c.parentElement.closest('.card')
                && !c.closest('.modal, .modal-backdrop, [role="dialog"]') && nameOf(c).length > 0;
        });
        var at = found.map(function (c, i) { var r = c.getBoundingClientRect(); return { c: c, top: r.top, left: r.left, i: i }; });
        at.sort(function (a, b) {
            return Math.abs(a.top - b.top) >= 4 ? a.top - b.top : a.left - b.left || a.i - b.i;
        });
        return at.map(function (a) { return a.c; });
    }

    // By index, not node: a re-render replaces the cards under the same links.
    function link(i, name, cls) {
        var a = document.createElement('a');
        a.href = '#';
        a.className = cls;
        a.textContent = name;
        a.addEventListener('click', function (e) {
            e.preventDefault();
            var card = cards[i];
            if (!card) return;
            pinned = i;
            clickPin = true;
            aim = i;
            aimTries = 0;
            place();
            jumpTo(card);
        });
        return a;
    }

    // fade: the index is going away rather than being replaced, so let it fade out.
    function clear(fade) {
        var gone = function (n) {
            if (!fade) return n.remove();
            n.classList.add('card-index-out');
            setTimeout(function () { n.remove(); }, 150);
        };
        if (list) { gone(list); list = null; }
        // Every row, not just the one held: a re-render can leave a copy behind.
        Array.prototype.forEach.call(document.querySelectorAll('.card-index-row:not(.card-index-out)'), gone);
        row = null;
        pinned = -1;
        shown = -1;
        stepFrom = null;
        if (scroller) scroller.removeEventListener('scroll', onScroll);
        scroller = null;
        cards = [];
    }

    // Before the first card, unless it sits in a grid or a side-by-side flex row: then before the
    // ancestor that does not, so the row never takes a grid cell or squeezes a column.
    function rowAnchor(card, root) {
        var at = card;
        while (at.parentElement && at.parentElement !== root) {
            var s = getComputedStyle(at.parentElement);
            var sideBySide = s.display.indexOf('grid') >= 0 || (s.display.indexOf('flex') >= 0 && s.flexDirection.indexOf('row') === 0);
            if (!sideBySide) break;
            at = at.parentElement;
        }
        return at;
    }

    function build() {
        pending = 0;
        var root = content();
        if (!root) return clear();
        var found = root.querySelector('[data-no-card-index]') ? [] : findCards(root);
        var sc = found.length ? scrollerOf(found[0]) : null;
        // Worth it once a card starts below the first screen: two cards are enough when the second is
        // far down (Device Stats under its charts); three that all fit on screen hide nothing.
        var hidden = sc && found.some(function (c) {
            return c.getBoundingClientRect().top - paneRect(sc).top + sc.scrollTop > sc.clientHeight;
        });
        var names = found.length >= MIN_CARDS ? found.map(nameOf).join('|') : '';
        // Once shown, the same cards keep it: a page near one screen tall (Edit, as its Problems list
        // grows and shrinks) would otherwise add and drop it, shifting the whole page each time.
        var sig = hidden || (names && names === signature) ? names : '';
        var gutter = root.getBoundingClientRect().left - (sc ? paneRect(sc).left : 0);
        var mode = gutter >= LIST_WIDTH + 2 * GAP + 8 ? 'list' : 'row';
        // Same cards, same form, row still in the page: keep the index, follow the new nodes.
        if (sig === signature && (sig === '' || (mode === 'list' ? list : row && row.isConnected))) {
            if (sig) cards = found;
            place();
            return;
        }
        var keep = pinned >= 0 && cards[pinned] ? nameOf(cards[pinned]) : '';
        clear(!sig);
        signature = sig;
        if (!sig) return;
        cards = found;
        if (keep) pinned = found.map(nameOf).indexOf(keep);
        scroller = sc;
        if (mode === 'list') {
            list = document.createElement('nav');
            list.className = 'card-index';
            list.setAttribute('aria-label', 'On this page');
            var label = document.createElement('div');
            label.className = 'card-index-label';
            label.textContent = 'On this page';
            list.appendChild(label);
            cards.forEach(function (c, i) { list.appendChild(link(i, nameOf(c), 'card-index-link')); });
            // The list sits on <body>, outside the scroller: a wheel over it would scroll nothing.
            list.addEventListener('wheel', function (e) {
                if (!scroller) return;
                e.preventDefault();
                var unit = e.deltaMode === 1 ? 16 : e.deltaMode === 2 ? scroller.clientHeight : 1;
                scroller.scrollBy({ top: e.deltaY * unit, left: 0 });
            }, { passive: false });
            document.body.appendChild(list);
        } else {
            row = document.createElement('nav');
            row.className = 'card-index-row';
            row.setAttribute('aria-label', 'On this page');
            cards.forEach(function (c, i) { row.appendChild(link(i, nameOf(c), 'card-index-link')); });
            var anchor = rowAnchor(cards[0], root);
            anchor.parentElement.insertBefore(row, anchor);
        }
        scroller.addEventListener('scroll', onScroll, { passive: true });
        place();
    }

    // The card in view: the last whose top has passed a reading line. Cards side by side share a top,
    // so they light up together; a click lights only the card clicked.
    function place(towards) {
        if (!scroller || !cards.length) return;
        var pane = paneRect(scroller);
        var height = pane.bottom - pane.top;
        var toEnd = scroller.scrollHeight - scroller.clientHeight - scroller.scrollTop;
        // Just under the top, so a card scrolled there (a deep link, block: 'start') is the one lit even
        // when it is short. Over the last two thirds of a screen the line moves down to the bottom, so
        // the highlight only moves forward and the last cards still get their turn.
        var line = Math.min(LINE, scroller.scrollTop);
        var zone = height * 2 / 3;
        if (toEnd < zone) line = Math.max(line, LINE + (height - LINE) * (1 - toEnd / zone));
        var tops = cards.map(function (c) { return c.getBoundingClientRect().top; });
        var activeTop = tops[0];
        tops.forEach(function (t) { if (t <= pane.top + line) activeTop = Math.max(activeTop, t); });
        var natural = tops.findIndex(function (t) { return Math.abs(t - activeTop) < 4; });
        // The highlight moves with the scroll, never against it: leaving a click's pin near the end,
        // where the line sits low, would otherwise drop it to a later card while scrolling up.
        var prevTop = lastTop;
        var dir = scroller.scrollTop - lastTop || towards || 0;
        lastTop = scroller.scrollTop;
        if (pinned >= 0 || dir < 0) stepFrom = null;
        // Scrolling down near the end from a card the line has already left behind (a click there)
        // would jump to the last card: the rows after it share the scroll that is left instead.
        var rowOf = function (idx) {
            var n = 0;
            for (var q = 1; q <= idx; q++) if (Math.abs(tops[q] - tops[q - 1]) >= 4) n++;
            return n;
        };
        if (!stepFrom && pinned < 0 && dir > 0 && shown >= 0 && shown < cards.length && toEnd < zone && rowOf(natural) > rowOf(shown) + 1) {
            stepFrom = { top: prevTop, idx: shown };
        }
        if (stepFrom && pinned < 0 && dir > 0) {
            var rows = [];
            for (var r = stepFrom.idx + 1; r < cards.length; r++) {
                var prevRow = rows.length ? tops[rows[rows.length - 1]] : tops[stepFrom.idx];
                if (Math.abs(tops[r] - prevRow) >= 4) rows.push(r);
            }
            // One row at a time: when the scroll that is left says so, or per wheel turn once nothing
            // is left to scroll.
            var span = scroller.scrollHeight - scroller.clientHeight - stepFrom.top;
            var k = span > 0 ? Math.floor((scroller.scrollTop - stepFrom.top) / span * rows.length + 0.001) : rows.length;
            var rank = rows.indexOf(shown) + 1;
            var next = rows.filter(function (q) { return tops[q] > tops[shown] + 4; })[0];
            var moved = scroller.scrollTop !== prevTop;
            natural = next !== undefined && (moved ? k > rank : true) ? Math.min(natural, next) : shown;
        }
        if (pinned >= 0) shown = pinned;
        else if (shown < 0 || shown >= cards.length || (dir < 0 ? natural < shown : dir > 0 ? natural > shown : false)) shown = natural;
        var links = (list || row).querySelectorAll('.card-index-link');
        Array.prototype.forEach.call(links, function (a, i) {
            a.classList.toggle('active', pinned >= 0 ? i === pinned : Math.abs(tops[i] - tops[shown]) < 4);
        });
        if (list) {
            var root = content().getBoundingClientRect();
            var width = Math.max(LIST_WIDTH, Math.min(LIST_MAX, root.left - pane.left - 2 * GAP - 8));
            list.style.width = width + 'px';
            list.style.left = Math.max(GAP, root.left - width - GAP) + 'px';
            // Level with the first card, and never beside the back button floating in the same gutter.
            var top = Math.max(pane.top + 16, cards[0].getBoundingClientRect().top);
            var back = document.querySelector('.page-back-float .page-back-btn');
            if (back && back.offsetParent !== null) top = Math.max(top, back.getBoundingClientRect().bottom + 16);
            list.style.top = top + 'px';
        }
    }

    // A scroll no scroll input started is the page's own (a deep link's scrollIntoView): pin the first
    // card at or below the top, which a card near the end could not otherwise win.
    // A click's own smooth scroll lasts until its scroll events stop, however long that takes; a
    // fixed window let the tail of a long one re-pin to the card below. A click's pin gives way only
    // to a deep link's scrollIntoView, never to a scroll the browser makes on its own.
    var lastInput = 0, own = false, ownTimer = 0, clickPin = false;
    var aim = -1, aimTries = 0;
    function ownScrolling() {
        own = true;
        clearTimeout(ownTimer);
        ownTimer = setTimeout(settled, 200);
    }
    function jumpTo(card) {
        ownScrolling();
        var sc = scrollerOf(card);
        var top = card.getBoundingClientRect().top - paneRect(sc).top;
        sc.scrollTo({ top: sc.scrollTop + top - 16, behavior: 'smooth' });
    }
    // Content above can change height while the smooth scroll runs (a data refresh), leaving it short
    // or past the card; once it settles, go again until the card is where it was sent.
    function settled() {
        own = false;
        var card = aim >= 0 && pinned === aim ? cards[aim] : null;
        if (!card || !scroller) { aim = -1; return; }
        var off = card.getBoundingClientRect().top - paneRect(scroller).top - 16;
        var toEnd = scroller.scrollHeight - scroller.clientHeight - scroller.scrollTop;
        var canMove = off < 0 ? scroller.scrollTop > 1 : toEnd > 1;
        if (Math.abs(off) > 4 && canMove && aimTries++ < 3) jumpTo(card);
        else aim = -1;
    }
    function onScroll() {
        if (own) ownScrolling();
        var target = Date.now() - intoViewAt < 1500 ? intoView : null;
        if (Date.now() - lastInput > 600 && !own && scroller && !(clickPin && !target)) {
            var pane = paneRect(scroller);
            var at = -1;
            // Only while that card is where the scroll put it, at the top or (near the end) on screen:
            // a reload's scroll restore moves on past a deep link.
            var atEnd = scroller.scrollHeight - scroller.clientHeight - scroller.scrollTop <= 2;
            for (var i = 0; target && i < cards.length; i++) {
                if (cards[i].contains(target) || target.contains(cards[i])) {
                    var r = cards[i].getBoundingClientRect();
                    var placed = atEnd ? r.top < pane.bottom && r.bottom > pane.top
                        : r.top <= pane.top + LINE && r.bottom > pane.top + LINE;
                    if (placed) at = i;
                    break;
                }
            }
            for (var j = 0; at < 0 && j < cards.length; j++) {
                if (cards[j].getBoundingClientRect().top >= pane.top - 8) at = j;
            }
            if (at >= 0) { pinned = at; clickPin = false; }
        }
        if (frame) return;
        frame = requestAnimationFrame(function () { frame = 0; place(); });
    }
    var frame = 0;

    // At most every 150 ms, not after a quiet 150 ms: a page with live charts never goes quiet.
    function schedule() {
        if (!pending) pending = setTimeout(build, 150);
    }

    // Deep links scroll with scrollIntoView; noting the element tells two cards that both end at the
    // page's bottom apart.
    var intoView = null, intoViewAt = 0;
    var nativeIntoView = Element.prototype.scrollIntoView;
    Element.prototype.scrollIntoView = function () {
        intoView = this;
        intoViewAt = Date.now();
        var result = nativeIntoView.apply(this, arguments);
        // Pinned here as well: a card already in place scrolls nothing, so no scroll event follows.
        for (var i = 0; i < cards.length && !own; i++) {
            if (cards[i].contains(this) || this.contains(cards[i])) { pinned = i; clickPin = false; onScroll(); break; }
        }
        return result;
    };

    function start() {
        // Blazor re-renders on navigation, tab switches, and data loads; the row this adds is ignored.
        // Class changes count too: Settings switches tabs by class, with every tab's cards rendered.
        new MutationObserver(function (records) {
            for (var i = 0; i < records.length; i++) {
                var t = records[i].target;
                if (!(row && (t === row || row.contains(t))) && !(list && list.contains(t))) { schedule(); return; }
            }
        }).observe(document.body, { childList: true, subtree: true, attributes: true, attributeFilter: ['class', 'hidden'] });
        // The reader scrolling themselves ends a pin. A press on the scroller itself is its scrollbar;
        // any other click is not a scroll, so a deep link after it still pins.
        ['wheel', 'touchstart', 'keydown', 'pointerdown'].forEach(function (type) {
            window.addEventListener(type, function (e) {
                if (type === 'pointerdown' && e.target !== scroller && e.target !== document.documentElement) return;
                lastInput = Date.now();
                own = false;
                clickPin = false;
                aim = -1;
                // The wheel's direction stands in for a scroll that cannot happen (already at an end).
                var atEnd = scroller && scroller.scrollHeight - scroller.clientHeight - scroller.scrollTop <= 1;
                if (pinned >= 0) { pinned = -1; place(type === 'wheel' ? Math.sign(e.deltaY) : 0); }
                else if (type === 'wheel' && e.deltaY > 0 && atEnd) place(1);
            }, { passive: true, capture: true });
        });
        // A resize can change the form (list or row), so rebuild even with the same cards.
        window.addEventListener('resize', function () { signature = null; schedule(); });
        schedule();
    }

    if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', start);
    else start();
})();
