// Change Control's YAML editor: CodeMirror 6 with completion from the site's own config and
// diagnostics from the server's validation. One editor per host element, kept on host._cc.
import {
    EditorView, EditorState, Compartment, basicSetup, keymap, indentWithTab, indentUnit, yaml,
    autocompletion, startCompletion, closeCompletion, completionStatus, currentCompletions, setSelectedCompletion,
    setDiagnostics, lintGutter, syntaxHighlighting, HighlightStyle, tags,
    Decoration, WidgetType, ViewPlugin, MatchDecorator, StateEffect, StateField, Prec,
    codeFolding, foldEffect, unfoldEffect, foldedRanges,
} from '../lib/codemirror/codemirror.bundle.js?v=7';

const highlight = HighlightStyle.define([
    { tag: [tags.propertyName, tags.definition(tags.propertyName)], color: 'var(--info-color)' },
    { tag: [tags.string, tags.special(tags.string)], color: 'var(--text-primary)' },
    { tag: [tags.number, tags.integer, tags.float], color: '#f59e0b' },
    { tag: [tags.bool, tags.null, tags.atom, tags.keyword], color: '#a78bfa' },
    { tag: tags.comment, color: 'var(--text-muted)', fontStyle: 'italic' },
    { tag: [tags.punctuation, tags.separator, tags.squareBracket, tags.brace], color: 'var(--text-secondary)' },
]);

const theme = EditorView.theme({
    '&': { backgroundColor: 'transparent', color: 'var(--text-primary)', fontSize: '0.8125rem', maxHeight: '560px' },
    '.cm-scroller': { fontFamily: 'ui-monospace, SFMono-Regular, Menlo, Consolas, monospace', lineHeight: '1.6', overflow: 'auto' },
    '.cm-content': { caretColor: 'var(--text-primary)', minHeight: '480px', padding: '0.75rem 0' },
    '.cm-gutters': { backgroundColor: 'transparent', color: 'var(--text-muted)', border: 'none' },
    '.cm-activeLine': { backgroundColor: 'color-mix(in srgb, var(--bg-hover) 50%, transparent)' },
    '.cm-activeLineGutter': { backgroundColor: 'transparent', color: 'var(--text-secondary)' },
    '&.cm-focused': { outline: 'none' },
    '&.cm-focused .cm-cursor': { borderLeftColor: 'var(--text-primary)' },
    '.cm-selectionBackground, &.cm-focused .cm-selectionBackground, ::selection': { backgroundColor: 'color-mix(in srgb, var(--info-color) 25%, transparent)' },
    '.cm-tooltip': { backgroundColor: 'var(--bg-tertiary)', border: '1px solid var(--bg-hover)', color: 'var(--text-primary)' },
    '.cm-tooltip-autocomplete > ul > li[aria-selected]': { backgroundColor: 'var(--primary-color)', color: 'var(--text-primary)' },
    '.cm-completionDetail': { color: 'var(--text-secondary)', fontStyle: 'normal', marginLeft: '0.75rem' },
    '.cm-diagnostic-error': { borderLeftColor: 'var(--danger-color)' },
    '.cm-diagnostic-warning': { borderLeftColor: 'var(--warning-color)' },
    '.cm-diagnostic-info': { borderLeftColor: 'var(--info-color)' },
    '.cc-secret': {
        display: 'inline-flex', alignItems: 'center', gap: '0.4rem', padding: '0 0.4rem', borderRadius: '4px',
        backgroundColor: 'var(--bg-tertiary)', color: 'var(--text-secondary)', letterSpacing: '0.1em',
    },
    // The gap before an end-of-line button is the wrapper's padding, never a margin: the cursor at the
    // line's end is drawn at the widget's edge, and a margin put it where a trailing space would be.
    '.cc-line-tail': { paddingLeft: '0.75rem' },
    '.cc-managed': { color: 'var(--text-muted)' },
    // Inline and in the line's own height. Not cc-order-*: app.css styles those (Changes' policy
    // move rows), and its ".cc-order-move + .cc-order-move" margin doubled each policy's first line.
    '.cc-policy-rank': { whiteSpace: 'nowrap' },
    '.cc-policy-step': {
        font: 'inherit', lineHeight: 'inherit', verticalAlign: 'baseline', height: 'auto', margin: '0', padding: '0 0.25rem', cursor: 'pointer',
        border: 'none', borderRadius: '3px', background: 'transparent', color: 'var(--text-muted)',
    },
    '.cc-policy-step:hover:not(:disabled)': { color: 'var(--text-primary)', background: 'var(--bg-tertiary)' },
    '.cc-policy-step:disabled': { opacity: '0.3', cursor: 'default' },
    '.cc-policy-rank-label': { fontSize: '0.75rem', color: 'var(--text-muted)', marginLeft: '0.35rem' },
    '.cc-managed-label': { fontSize: '0.75rem', padding: '0 0.4rem', borderRadius: '3px', backgroundColor: 'var(--bg-tertiary)', color: 'var(--text-secondary)' },
    '.cc-remove-item': {
        font: 'inherit', fontSize: '0.95rem', lineHeight: '1', padding: '0 0.35rem', cursor: 'pointer',
        border: 'none', borderRadius: '3px', background: 'transparent', color: 'var(--text-muted)',
    },
    '.cc-remove-item:hover, .cc-remove-item:focus-visible': { color: 'var(--danger-color)', background: 'var(--bg-tertiary)' },
    '.cc-collapse-children': {
        font: 'inherit', fontSize: '0.75rem', lineHeight: '1.4', padding: '0 0.4rem', cursor: 'pointer',
        border: 'none', borderRadius: '3px', background: 'transparent', color: 'var(--text-muted)',
    },
    '.cc-collapse-children:hover, .cc-collapse-children:focus-visible': { color: 'var(--text-primary)', background: 'var(--bg-tertiary)' },
    '.cm-foldPlaceholder': {
        margin: '0 0.4rem', padding: '0 0.4rem', border: 'none', borderRadius: '3px',
        backgroundColor: 'var(--bg-tertiary)', color: 'var(--text-secondary)', cursor: 'pointer',
    },
    '.cc-add-item': {
        font: 'inherit', fontSize: '0.75rem', lineHeight: '1.6', margin: '0.1rem 0 0.35rem', padding: '0 0.4rem', cursor: 'pointer',
        border: '1px dashed var(--bg-hover)', borderRadius: '3px', background: 'transparent', color: 'var(--info-color)',
    },
    '.cc-add-item:hover, .cc-add-item:focus-visible': { background: 'var(--bg-tertiary)', borderStyle: 'solid' },
    '.cc-add-item:disabled': { opacity: '0.6', cursor: 'default' },
    '.cc-secret button': {
        font: 'inherit', fontSize: '0.75rem', letterSpacing: 'normal', padding: '0 0.35rem', cursor: 'pointer',
        border: '1px solid var(--bg-hover)', borderRadius: '3px', background: 'transparent', color: 'var(--info-color)',
    },
    // Find / replace in the app's colors and shapes, kept compact: smaller than the app's standard
    // controls, larger than CodeMirror's default. Scoped under .cm-search to outrank its gradient buttons.
    '.cm-panels': { backgroundColor: 'var(--bg-secondary)', color: 'var(--text-primary)' },
    '.cm-panels.cm-panels-top': { borderBottom: '1px solid var(--border-color)' },
    '.cm-panels.cm-panels-bottom': { borderTop: '1px solid var(--border-color)' },
    '.cm-panel.cm-search': {
        display: 'flex', flexWrap: 'wrap', alignItems: 'center', gap: '0.4rem',
        padding: '0.5rem 2rem 0.5rem 0.6rem', fontSize: '0.8125rem', position: 'relative',
    },
    '.cm-panel.cm-search br': { display: 'block', width: '100%', height: '0', content: '""' },
    '.cm-panel.cm-search .cm-textfield': {
        font: 'inherit', fontSize: '0.8125rem', height: '26px', width: '13rem', maxWidth: '100%', margin: '0', padding: '0 0.5rem',
        background: 'var(--bg-primary)', border: '1px solid var(--border-color)', borderRadius: 'var(--border-radius)', color: 'var(--text-primary)',
    },
    '.cm-panel.cm-search .cm-textfield:focus': {
        outline: 'none', borderColor: 'var(--primary-color)', boxShadow: '0 0 0 2px rgba(5, 89, 201, 0.2)',
    },
    '.cm-panel.cm-search .cm-button': {
        font: 'inherit', fontSize: '0.75rem', fontWeight: '500', height: '26px', margin: '0', padding: '0 9px', cursor: 'pointer',
        border: 'none', borderRadius: 'var(--border-radius)', backgroundImage: 'none', backgroundColor: 'var(--bg-tertiary)', color: 'var(--text-primary)',
    },
    '.cm-panel.cm-search .cm-button:hover': { backgroundColor: 'var(--bg-elevated)' },
    '.cm-panel.cm-search .cm-button:active': { backgroundImage: 'none' },
    '.cm-panel.cm-search label': {
        display: 'inline-flex', alignItems: 'center', gap: '0.3rem', margin: '0', fontSize: '0.75rem', color: 'var(--text-secondary)',
    },
    '.cm-panel.cm-search input[type=checkbox]': { margin: '0', accentColor: 'var(--primary-color)' },
    '.cm-panel.cm-search [name=close]': {
        position: 'absolute', top: '0.4rem', right: '0.4rem', padding: '0.1rem 0.4rem', fontSize: '1rem', lineHeight: '1',
        border: 'none', borderRadius: '4px', background: 'transparent', color: 'var(--text-muted)', cursor: 'pointer',
    },
    '.cm-panel.cm-search [name=close]:hover': { color: 'var(--text-primary)', background: 'var(--bg-tertiary)' },
}, { dark: true });

// The search panel's labels in the app's Title Case, as its buttons read elsewhere.
const phrases = EditorState.phrases.of({
    'next': 'Next', 'previous': 'Previous', 'all': 'Select All', 'match case': 'Match Case',
    'regexp': 'Regex', 'by word': 'By Word', 'replace': 'Replace', 'replace all': 'Replace All',
});

// ---------- Indentation ----------

/**
 * The indent for a new line typed after `text`: one level in after "key:", the item's own keys after
 * "- ", else the same. lang-yaml indents from its parse tree, which an unfinished "key:" leaves
 * unreliable. Pure: exported for testing.
 */
export function yamlIndentAfter(text) {
    if (!text.trim()) return text.length;
    const lead = /^(\s*)((?:- ?)*)/.exec(text);
    const indent = lead[1].length + lead[2].length + (lead[2].endsWith('-') ? 1 : 0);
    const body = text.slice(lead[0].length).replace(/\s+#.*$/, '').trimEnd();
    return /:$/.test(body) || /:\s*[|>][-+0-9]*$/.test(body) ? indent + 2 : indent;
}

function yamlNewline(view) {
    const { state } = view;
    if (state.selection.ranges.length !== 1 || !state.selection.main.empty) return false;
    const head = state.selection.main.head;
    const line = state.doc.lineAt(head);
    const before = line.text.slice(0, head - line.from);
    const restSpaces = /^[ \t]*/.exec(line.text.slice(head - line.from))[0].length;
    const insert = '\n' + ' '.repeat(yamlIndentAfter(before));
    view.dispatch({
        changes: { from: head - (before.length - before.trimEnd().length), to: head + restSpaces, insert },
        selection: { anchor: head - (before.length - before.trimEnd().length) + insert.length },
        scrollIntoView: true, userEvent: 'input',
    });
    return true;
}

// ---------- Masked secrets ----------

// A masked secret, <secret:path>, names the value's place in the config the edit started from. The
// value stays on the server: Show (Site Admin, audited) swaps the token for it; anyone who can edit
// can delete the token and type or generate a new value without seeing the old one.
class SecretWidget extends WidgetType {
    constructor(token, state) {
        super();
        this.token = token;
        this.state = state;
    }

    eq(other) { return other.token === this.token && other.state.canReveal === this.state.canReveal; }

    toDOM(view) {
        const chip = document.createElement('span');
        chip.className = 'cc-secret';
        chip.setAttribute('aria-label', this.state.canReveal ? 'Masked secret' : 'Masked secret: delete it to enter a new value');
        chip.append('••••••••');
        if (this.state.canReveal) {
            const show = document.createElement('button');
            show.type = 'button';
            show.textContent = 'Show';
            show.addEventListener('mousedown', e => e.preventDefault());
            show.addEventListener('click', async e => {
                e.preventDefault();
                show.disabled = true;
                try {
                    const value = await this.state.dotnet.invokeMethodAsync('RevealSecret', this.token);
                    if (value == null) { show.textContent = 'Unavailable'; return; }
                    const at = view.posAtDOM(chip);
                    const doc = view.state.doc.toString();
                    // The chip's own range when it is still there; the token may have moved if the doc changed meanwhile.
                    const from = doc.startsWith(this.token, at) ? at : doc.indexOf(this.token);
                    if (from < 0) return;
                    // Cursor onto the value so it shows; leaving the line unedited puts the chip back.
                    this.state.revealed.push({ from, to: from + value.length, token: this.token, value });
                    view.dispatch({ changes: { from, to: from + this.token.length, insert: value }, selection: { anchor: from + value.length } });
                    view.focus();
                } catch (err) {
                    console.warn('cc-editor: reveal failed', err);
                    show.textContent = 'Not allowed';
                }
            });
            chip.append(show);
        }
        return chip;
    }

    ignoreEvent(e) { return e.type === 'mousedown' || e.type === 'click'; }
}

function secretMasks(state) {
    const matcher = new MatchDecorator({
        regexp: /<secret:[^>\s]+>/g,
        decoration: m => Decoration.replace({ widget: new SecretWidget(m[0], state) }),
    });
    const plugin = ViewPlugin.fromClass(class {
        constructor(view) { this.decorations = matcher.createDeco(view); }
        update(u) { this.decorations = matcher.updateDeco(u, this.decorations); }
    }, { decorations: v => v.decorations });
    // Atomic: the cursor steps over a token and Backspace removes it whole, never half a path.
    return [plugin, EditorView.atomicRanges.of(view => view.plugin(plugin)?.decorations ?? Decoration.none)];
}

// A secret's value in clear (revealed, typed, or generated) shows only while the cursor is on its line.
class LiteralMaskWidget extends WidgetType {
    eq() { return true; }

    toDOM() {
        const mask = document.createElement('span');
        mask.className = 'cc-secret';
        mask.setAttribute('aria-label', 'Secret value: put the cursor on this line to see it');
        mask.append('••••••••');
        return mask;
    }

    // Clicks fall through to the editor, which puts the cursor on the line and so unmasks it.
    ignoreEvent() { return false; }
}

const refreshMasks = StateEffect.define();

/**
 * Ranges of secret-field values in clear on the given lines, except the cursor's line. Tokens and
 * <keep current> are left to the token chips. Pure: the Node harness tests it.
 */
export function secretValueRanges(cmState, schema, fromLine, toLine) {
    const ranges = [];
    if (!schema) return ranges;
    const doc = cmState.doc;
    const cursorLine = doc.lineAt(cmState.selection.main.head).number;
    for (let n = Math.max(1, fromLine); n <= Math.min(doc.lines, toLine); n++) {
        if (n === cursorLine) continue;
        const line = doc.line(n);
        const m = /^(\s*)(- )?([A-Za-z0-9_.-]+):[ \t]+(.*)$/.exec(line.text);
        if (!m) continue;
        const value = scalarText(m[4]);
        if (!value || value.startsWith('<secret:') || value === '<keep current>') continue;
        const indent = m[1].length + (m[2] ? 2 : 0);
        if (!fieldAt(schema, doc, n, indent, m[3])?.secret) continue;
        const start = line.text.length - m[4].length;
        ranges.push({ from: line.from + start, to: line.from + start + value.length });
    }
    return ranges;
}

// A YAML scalar without its trailing comment. A quoted value ends at its closing quote, so a '#'
// inside a password is part of the value, not a comment.
function scalarText(rest) {
    const q = rest[0];
    if (q === '"' || q === "'") {
        for (let i = 1; i < rest.length; i++) {
            if (q === '"' && rest[i] === '\\') { i++; continue; }
            if (rest[i] === q) {
                if (q === "'" && rest[i + 1] === "'") { i++; continue; }
                return rest.slice(0, i + 1);
            }
        }
        return rest.trimEnd();
    }
    return rest.replace(/\s+#.*$/, '').trimEnd();
}

function literalMasks(state) {
    const build = view => {
        const ranges = [];
        for (const { from, to } of view.visibleRanges) {
            const doc = view.state.doc;
            ranges.push(...secretValueRanges(view.state, state.schema, doc.lineAt(from).number, doc.lineAt(to).number));
        }
        return Decoration.set(ranges.map(r => Decoration.replace({ widget: new LiteralMaskWidget() }).range(r.from, r.to)), true);
    };
    return ViewPlugin.fromClass(class {
        constructor(view) { this.decorations = build(view); }
        update(u) {
            if (u.docChanged || u.selectionSet || u.viewportChanged || u.transactions.some(t => t.effects.some(e => e.is(refreshMasks)))) {
                this.decorations = build(u.view);
            }
        }
    }, { decorations: v => v.decorations });
}

// ---------- Removing an object ----------

/**
 * Each top-level object's block: from its "- " line to the line before the next object or collection
 * key, with its collection's YAML name and a label from its first field. Pure: the Node harness tests it.
 */
export function itemBlocks(doc) {
    const blocks = [];
    let collection = null;
    let open = null;
    const close = endLine => {
        if (open && endLine >= open.line) blocks.push({ ...open, to: doc.line(endLine).to });
        open = null;
    };
    for (let n = 1; n <= doc.lines; n++) {
        const text = doc.line(n).text;
        // An empty collection ("acl_rules: []") is a key too: missed, the object before it ran on
        // through it, and its fold, x, and order arrows took the collections after it along.
        const key = /^([A-Za-z0-9_]+):\s*(\[\]|\{\})?\s*$/.exec(text);
        if (key) {
            close(n - 1);
            collection = key[1];
            continue;
        }
        // A comment at the margin (Firewall Policies' zone pair headings) belongs to what follows it,
        // so removing or moving the object above leaves it in place.
        if (collection && text.startsWith('#')) {
            close(n - 1);
            continue;
        }
        if (collection && text.startsWith('- ')) {
            close(n - 1);
            const field = t => {
                const m = /^(?:- |  )([A-Za-z0-9_.-]+):\s*(.*)$/.exec(t);
                return m ? { key: m[1], value: scalarText(m[2]).replace(/^["']|["']$/g, '') } : null;
            };
            const first = field(text);
            // The editor writes "- id: ..." first; the object's name is on the next line.
            const id = first?.key === 'id' ? first.value : null;
            const named = id && n < doc.lines ? field(doc.line(n + 1).text) : first;
            // A blank first field (a new object from Add) names nothing yet.
            open = { from: doc.line(n).from, line: n, lineEnd: doc.line(n).to, collection, id, label: named?.value || 'this object' };
        }
    }
    close(doc.lines);
    return blocks;
}

class RemoveWidget extends WidgetType {
    constructor(block) {
        super();
        this.block = block;
    }

    eq(other) { return other.block.from === this.block.from && other.block.to === this.block.to && other.block.label === this.block.label && other.block.id === this.block.id; }

    toDOM(view) {
        const button = document.createElement('button');
        button.type = 'button';
        button.className = 'cc-remove-item';
        button.textContent = '×';
        button.setAttribute('aria-label', `Remove ${this.block.label}`);
        button.dataset.tooltip = `Remove ${this.block.label}`;
        button.addEventListener('mousedown', e => e.preventDefault());
        button.addEventListener('click', e => {
            e.preventDefault();
            const doc = view.state.doc;
            // Take the line break with the block; at the end of the file, the one before it.
            const from = this.block.to < doc.length ? this.block.from : Math.max(0, this.block.from - 1);
            const to = this.block.to < doc.length ? this.block.to + 1 : this.block.to;
            view.dispatch({ changes: { from, to }, userEvent: 'delete.remove-object' });
            view.focus();
        });
        return lineTail(button);
    }

    ignoreEvent(e) { return e.type === 'mousedown' || e.type === 'click'; }
}

// An × at the end of each object's first line, except in collections Network Optimizer cannot delete
// from and on objects no plan deletes (UniFi's predefined and built-in objects, Threat Intelligence's).
function removeButtons(state) {
    const build = view => Decoration.set(itemBlocks(view.state.doc)
        .filter(b => !state.schema?.collections?.[b.collection]?.readOnly
            && !(b.id && state.schema?.collections?.[b.collection]?.undeletable?.includes(b.id)))
        .map(b => Decoration.widget({ widget: new RemoveWidget(b), side: 1 }).range(b.lineEnd)), true);
    return ViewPlugin.fromClass(class {
        constructor(view) { this.decorations = build(view); }
        update(u) {
            if (u.docChanged || u.transactions.some(t => t.effects.some(e => e.is(refreshMasks)))) this.decorations = build(u.view);
        }
    }, { decorations: v => v.decorations });
}

// ---------- Read-only objects ----------

// UniFi's own objects and Network Optimizer's (the schema's "managed", with who manages each) are
// read-only: their lines refuse edits, show muted, and carry a label where the x would be. The
// server rejects an edit to them too (an upload, a paste), so this is the convenience, not the guard.
const managedBy = (state, b) => b.id ? state.schema?.collections?.[b.collection]?.managed?.[b.id] : undefined;

class ManagedWidget extends WidgetType {
    constructor(who) { super(); this.who = who; }
    eq(other) { return other.who === this.who; }
    toDOM() {
        const label = document.createElement('span');
        label.className = 'cc-managed-label';
        label.textContent = 'Read-only';
        label.dataset.tooltip = this.who === 'UniFi'
            ? 'Managed by UniFi; Change Control does not edit it.'
            : `Managed by ${this.who}; change it there.`;
        const tail = document.createElement('span');
        tail.className = 'cc-line-tail';
        tail.appendChild(label);
        return tail;
    }
    ignoreEvent() { return true; }
}

function managedObjects(state) {
    const build = doc => {
        const ranges = [];
        for (const b of itemBlocks(doc)) {
            const who = managedBy(state, b);
            if (!who) continue;
            for (let n = doc.lineAt(b.from).number, last = doc.lineAt(b.to).number; n <= last; n++)
                ranges.push(Decoration.line({ class: 'cc-managed' }).range(doc.line(n).from));
            ranges.push(Decoration.widget({ widget: new ManagedWidget(who), side: 1 }).range(b.lineEnd));
        }
        return Decoration.set(ranges, true);
    };
    const field = StateField.define({
        create: s => build(s.doc),
        update: (deco, tr) => tr.docChanged || tr.effects.some(e => e.is(refreshMasks)) ? build(tr.state.doc) : deco,
        provide: f => EditorView.decorations.from(f),
    });
    // A change inside one is dropped. Its first character stays open, so removing the object above
    // (which takes the line break before this one) is not caught at the boundary.
    // The editor's own actions pass: they never change a locked object's text, but an add lands at
    // the file's end, inside a locked policy that ends it (the grouped export often ends on one).
    const own = ['input.add-object', 'input.move-policy', 'input.match-fields', 'delete.remove-object'];
    const lock = EditorState.changeFilter.of(tr => {
        if (state.silent || !tr.docChanged || own.some(e => tr.isUserEvent(e))) return true;
        const locked = [];
        for (const b of itemBlocks(tr.startState.doc)) if (managedBy(state, b)) locked.push(b.from + 1, b.to);
        return locked.length ? locked : true;
    });
    return [field, lock];
}

// ---------- Firewall policy order ----------

// A policy's order is its place in the file among the policies of its zone pair: the file carries no
// index (PlanBuilder.AddPolicyOrder). Each policy's first line says where it stands, with arrows that
// swap it with its neighbor in that pair. UniFi's predefined policies sit outside the custom order.

/** Firewall policies in file order, each with its zone pair ("Internal → External"); pure, exported for testing. */
export function policyOrder(doc, isOutside = () => false) {
    const zoneOf = (lines, side) => {
        const at = lines.findIndex(l => new RegExp(`^\\s+${side}:\\s*$`).test(l));
        if (at < 0) return null;
        const pad = lines[at].length - lines[at].trimStart().length;
        for (let i = at + 1; i < lines.length; i++) {
            const l = lines[i];
            if (l.trim() && l.length - l.trimStart().length <= pad) break;
            const m = /^\s+zone(?:_id)?:\s*(.*?)\s*$/.exec(l);
            if (m) return m[1].replace(/^["']|["']$/g, '') || null;
        }
        return null;
    };
    const policies = itemBlocks(doc).filter(b => b.collection === 'firewall_policies' && !isOutside(b))
        .map(b => {
            const lines = doc.sliceString(b.from, b.to).split('\n');
            const src = zoneOf(lines, 'source');
            const dst = zoneOf(lines, 'destination');
            // A new policy has no zones yet: its own group, unnamed, until both are set.
            return { block: b, pair: src && dst ? `${src} → ${dst}` : `?${b.from}`, named: !!(src && dst) };
        });
    const byPair = new Map();
    for (const p of policies) {
        if (!byPair.has(p.pair)) byPair.set(p.pair, []);
        byPair.get(p.pair).push(p);
    }
    for (const list of byPair.values()) list.forEach((p, i) => { p.rank = i + 1; p.of = list.length; p.prev = list[i - 1]?.block; p.next = list[i + 1]?.block; });
    return policies;
}

/** The change that moves block above prev (up) or below next (down) in the file; pure, exported for testing. */
export function moveBlock(doc, block, target, up) {
    const take = block.to < doc.length ? block.to + 1 : block.to;
    let text = doc.sliceString(block.from, take);
    if (!text.endsWith('\n')) text += '\n';
    if (up) return { changes: [{ from: target.from, insert: text }, { from: block.from, to: take }], selection: { anchor: target.from } };
    const at = target.to < doc.length ? target.to + 1 : target.to;
    const insert = target.to < doc.length ? text : '\n' + text.replace(/\n$/, '');
    const anchor = at - (take - block.from) + (target.to < doc.length ? 0 : 1);
    return { changes: [{ from: block.from, to: take }, { from: at, insert }], selection: { anchor } };
}

class OrderWidget extends WidgetType {
    constructor(p, state) { super(); this.p = p; this.state = state; }
    eq(o) { return o.p.pair === this.p.pair && o.p.named === this.p.named && o.p.rank === this.p.rank && o.p.of === this.p.of && !o.p.prev === !this.p.prev && !o.p.next === !this.p.next; }
    toDOM(view) {
        const tail = document.createElement('span');
        tail.className = 'cc-line-tail cc-policy-rank';
        const arrow = (label, has, up) => {
            const b = document.createElement('button');
            b.type = 'button';
            b.className = 'cc-policy-step';
            b.textContent = label;
            b.disabled = !has;
            b.setAttribute('aria-label', up ? 'Move up in its zone pair' : 'Move down in its zone pair');
            b.addEventListener('mousedown', e => e.preventDefault());
            b.addEventListener('click', e => {
                e.preventDefault();
                // From the document as it is now: CodeMirror keeps this DOM across edits that leave the
                // label equal, so positions captured when it was drawn can be stale.
                const at = view.posAtDOM(tail);
                const p = policyOrder(view.state.doc, x => managedBy(this.state, x) === 'UniFi').find(x => x.block.lineEnd === at);
                const target = up ? p?.prev : p?.next;
                if (!p || !target) return;
                view.dispatch({ ...moveBlock(view.state.doc, p.block, target, up), userEvent: 'input.move-policy', scrollIntoView: true });
                view.focus();
            });
            return b;
        };
        tail.append(arrow('↑', this.p.prev, true), arrow('↓', this.p.next, false));
        const label = document.createElement('span');
        label.className = 'cc-policy-rank-label';
        label.textContent = this.p.named ? `${this.p.pair}, ${this.p.rank} of ${this.p.of}` : `${this.p.rank} of ${this.p.of}`;
        tail.appendChild(label);
        return tail;
    }
    ignoreEvent() { return true; }
}

function policyOrderMarks(state) {
    // A locked policy (Network Optimizer's own) is labeled but has no arrows.
    const build = doc => Decoration.set(policyOrder(doc, b => managedBy(state, b) === 'UniFi')
        .map(p => managedBy(state, p.block) ? { ...p, prev: null, next: null } : p)
        .map(p => Decoration.widget({ widget: new OrderWidget(p, state), side: 2 }).range(p.block.lineEnd)), true);
    return StateField.define({
        create: s => build(s.doc),
        update: (deco, tr) => tr.docChanged || tr.effects.some(e => e.is(refreshMasks)) ? build(tr.state.doc) : deco,
        provide: f => EditorView.decorations.from(f),
    });
}

// ---------- Collapsing children ----------

/**
 * The list items under a parent key line ("port_profiles:", "  port_overrides:"), each as the range a
 * fold hides: the end of its first line to the end of its last. Top-level items sit at column 0, nested
 * ones two in from their key, as the export writes them. Single-line items have nothing to fold.
 * Pure: exported for testing.
 */
export function childRanges(doc, lineNo) {
    const m = /^(\s*)(- )?([A-Za-z0-9_.-]+):\s*$/.exec(doc.line(lineNo).text);
    if (!m) return [];
    const keyIndent = m[1].length + (m[2] ? 2 : 0);
    const itemIndent = keyIndent === 0 ? 0 : keyIndent + 2;
    const marker = ' '.repeat(itemIndent) + '- ';
    const ranges = [];
    let start = null;
    let last = null;
    const close = () => { if (start && last > start) ranges.push({ from: doc.line(start).to, to: doc.line(last).to }); };
    for (let n = lineNo + 1; n <= doc.lines; n++) {
        const text = doc.line(n).text;
        // Comments (Firewall Policies' zone pair headings) neither end the list nor belong to an item.
        if (!text.trim() || text.trimStart().startsWith('#')) continue;
        const lead = text.length - text.trimStart().length;
        if (text.startsWith(marker)) { close(); start = n; last = n; continue; }
        if (lead < itemIndent || (itemIndent === 0 && lead === 0)) break;
        if (start) last = n;
    }
    close();
    return ranges;
}

/** Under a heading comment at the margin: the items up to the next heading or key, as childRanges gives them; pure. */
export function groupRanges(doc, lineNo) {
    if (!doc.line(lineNo).text.startsWith('#')) return [];
    let end = doc.lines;
    for (let n = lineNo + 1; n <= doc.lines; n++) {
        const text = doc.line(n).text;
        if (text.startsWith('#') || /^[A-Za-z0-9_]+:/.test(text)) { end = n - 1; break; }
    }
    const ranges = [];
    let start = null;
    let last = null;
    const close = () => { if (start && last > start) ranges.push({ from: doc.line(start).to, to: doc.line(last).to }); };
    for (let n = lineNo + 1; n <= end; n++) {
        const text = doc.line(n).text;
        if (!text.trim()) continue;
        if (text.startsWith('- ')) { close(); start = n; last = n; continue; }
        if (start) last = n;
    }
    close();
    return ranges;
}

const foldableUnder = (doc, lineNo) => doc.line(lineNo).text.startsWith('#') ? groupRanges(doc, lineNo) : childRanges(doc, lineNo);

class CollapseWidget extends WidgetType {
    constructor(lineNo, folded) {
        super();
        this.lineNo = lineNo;
        this.folded = folded;
    }

    eq(other) { return other.lineNo === this.lineNo && other.folded === this.folded; }

    toDOM(view) {
        const button = document.createElement('button');
        button.type = 'button';
        button.className = 'cc-collapse-children';
        button.textContent = this.folded ? 'Expand All' : 'Collapse All';
        button.addEventListener('mousedown', e => e.preventDefault());
        button.addEventListener('click', e => {
            e.preventDefault();
            const ranges = foldableUnder(view.state.doc, view.state.doc.lineAt(view.posAtDOM(button)).number);
            const open = ranges.filter(r => !isFolded(view.state, r));
            view.dispatch({
                effects: open.length > 0 ? open.map(r => foldEffect.of(r)) : ranges.map(r => unfoldEffect.of(r)),
            });
        });
        return lineTail(button);
    }

    ignoreEvent(e) { return e.type === 'mousedown' || e.type === 'click'; }
}

/** An end-of-line button in a wrapper that carries the gap before it (see .cc-line-tail). */
function lineTail(button) {
    const tail = document.createElement('span');
    tail.className = 'cc-line-tail';
    tail.appendChild(button);
    return tail;
}

function isFolded(cmState, range) {
    let found = false;
    foldedRanges(cmState).between(range.from, range.to, (from, to) => { if (from === range.from && to === range.to) found = true; });
    return found;
}

// "Collapse All" / "Expand All" at the end of each parent line (and zone pair heading) whose list items can fold.
function collapseButtons() {
    const build = view => {
        const doc = view.state.doc;
        const widgets = [];
        for (let n = 1; n <= doc.lines; n++) {
            const text = doc.line(n).text;
            if (!/:\s*$/.test(text) && !text.startsWith('#')) continue;
            const ranges = foldableUnder(doc, n);
            if (ranges.length === 0) continue;
            const folded = ranges.every(r => isFolded(view.state, r));
            widgets.push(Decoration.widget({ widget: new CollapseWidget(n, folded), side: 1 }).range(doc.line(n).to));
        }
        return Decoration.set(widgets, true);
    };
    return ViewPlugin.fromClass(class {
        constructor(view) { this.decorations = build(view); }
        update(u) {
            if (u.docChanged || u.transactions.some(t => t.effects.some(e => e.is(foldEffect) || e.is(unfoldEffect)))) this.decorations = build(u.view);
        }
    }, { decorations: v => v.decorations });
}

// A folded object reads by its name, not its ID: "- id: ...  [Access] Guest …".
const namedFolds = codeFolding({
    preparePlaceholder: (cmState, range) => {
        const next = cmState.doc.lineAt(range.from).number + 1;
        if (next > cmState.doc.lines) return null;
        const m = /^\s+(?:name|key|description|host_name):\s*(.+)$/.exec(cmState.doc.line(next).text);
        return m ? scalarText(m[1]).replace(/^["']|["']$/g, '') : null;
    },
    placeholderDOM: (view, onclick, prepared) => {
        const span = document.createElement('span');
        span.className = 'cm-foldPlaceholder';
        span.textContent = prepared ? `${prepared} …` : '…';
        span.setAttribute('aria-label', 'Folded; click to expand');
        span.onclick = onclick;
        return span;
    },
});

// ---------- Adding an object ----------

/** The last line of each top-level collection's block, by YAML name. Pure: exported for testing. */
export function collectionEnds(doc) {
    const ends = [];
    let open = null;
    for (let n = 1; n <= doc.lines; n++) {
        const m = /^([A-Za-z0-9_]+):/.exec(doc.line(n).text);
        if (!m) continue;
        if (open) ends.push({ collection: open.collection, line: lastContent(doc, open.line, n - 1) });
        open = { collection: m[1], line: n };
    }
    if (open) ends.push({ collection: open.collection, line: lastContent(doc, open.line, doc.lines) });
    return ends;
}

function lastContent(doc, first, last) {
    for (let n = last; n > first; n--) if (doc.line(n).text.trim()) return n;
    return first;
}

class AddWidget extends WidgetType {
    constructor(collection, label, state) {
        super();
        this.collection = collection;
        this.label = label;
        this.state = state;
    }

    eq(other) { return other.collection === this.collection && other.label === this.label; }

    toDOM() {
        const wrap = document.createElement('div');
        const button = document.createElement('button');
        button.type = 'button';
        button.className = 'cc-add-item';
        button.textContent = `+ Add ${this.label}`;
        button.addEventListener('mousedown', e => e.preventDefault());
        button.addEventListener('click', e => {
            e.preventDefault();
            button.disabled = true;
            this.state.dotnet.invokeMethodAsync('AddObject', this.collection)
                .catch(err => console.warn('cc-editor: add failed', err))
                .finally(() => { button.disabled = false; });
        });
        wrap.append(button);
        return wrap;
    }

    ignoreEvent() { return true; }
}

// A "+ Add" line under each collection the schema marks addable. Block widgets must come from a field.
function addButtons(state) {
    const build = doc => Decoration.set(collectionEnds(doc)
        .filter(e => state.schema?.collections?.[e.collection]?.add)
        .map(e => Decoration.widget({ widget: new AddWidget(e.collection, state.schema.collections[e.collection].add, state), block: true, side: 1 })
            .range(doc.line(e.line).to)), true);
    return StateField.define({
        create: s => build(s.doc),
        update: (deco, tr) => tr.docChanged || tr.effects.some(e => e.is(refreshMasks)) ? build(tr.state.doc) : deco,
        provide: f => EditorView.decorations.from(f),
    });
}

// Revealed values left unedited go back to their token once the cursor leaves the line, so a reveal
// is never an edit. An edited one stays as typed (and is masked off its line like any secret value).
function restoreRevealed(view, state) {
    const doc = view.state.doc;
    const cursorLine = doc.lineAt(view.state.selection.main.head).number;
    const changes = [];
    state.revealed = state.revealed.filter(r => {
        if (r.from > r.to || r.to > doc.length) return false;
        if (doc.lineAt(r.from).number === cursorLine) return true;
        if (doc.sliceString(r.from, r.to) === r.value) changes.push({ from: r.from, to: r.to, insert: r.token });
        return false;
    });
    if (changes.length) setTimeout(() => view.dispatch({ changes }), 0);
}

// The text the page sees: a revealed value still as shown reads as its token, so a reveal the cursor
// has not left yet is no edit either. A value typed over no longer matches, so it is sent as typed.
// Streamed, never a plain argument: a large collection (Firewall Policies) is past SignalR's 32 KB
// message limit, which drops the call and with it every edit to that collection.
function reportChange(view, state, key) {
    const bytes = new TextEncoder().encode(reportedText(view, state));
    state.dotnet.invokeMethodAsync('OnEditorChanged', key, DotNet.createJSStreamReference(bytes))
        .catch(e => console.warn('cc-editor: change callback failed', e));
}

function reportedText(view, state) {
    const doc = view.state.doc;
    const kept = state.revealed
        .filter(r => r.from <= r.to && r.to <= doc.length && doc.sliceString(r.from, r.to) === r.value)
        .sort((a, b) => b.from - a.from);
    let text = doc.toString();
    for (const r of kept) text = text.slice(0, r.from) + r.token + text.slice(r.to);
    return text;
}

// ---------- Per collection: scroll, cursor, and folds ----------

// A collection's place is per scope (the viewer and the site or template, set by the page) and group.
const groupOf = key => (key ?? '').slice((key ?? '').indexOf('|') + 1);
const placeKey = (state, key) => `${state.scope ?? ''}|${groupOf(key)}`;

/** IDs of the objects whose fold is closed, by the "- id:" line the editor writes first. */
function foldedIds(cmState) {
    const byLineEnd = new Map(itemBlocks(cmState.doc).filter(b => b.id).map(b => [b.lineEnd, b.id]));
    const ids = [];
    foldedRanges(cmState).between(0, cmState.doc.length, from => { const id = byLineEnd.get(from); if (id) ids.push(id); });
    return ids;
}

function foldIds(view, ids) {
    const want = new Set(ids);
    if (want.size === 0) return;
    const doc = view.state.doc;
    const effects = itemBlocks(doc).filter(b => b.id && want.has(b.id))
        .map(b => ({ from: b.lineEnd, to: doc.line(lastContent(doc, b.line, doc.lineAt(b.to).number)).to }))
        .filter(r => r.to > r.from).map(r => foldEffect.of(r));
    if (effects.length) view.dispatch({ effects });
}

// Folds outlive the page in this browser, by object ID, so an edit or a reload keeps them. Storage
// can be missing (private window, blocked site data); the editor works the same without it.
function saveFolds(view, state) {
    if (!state.scope) return;
    const name = `cc-editor-folds:${placeKey(state, state.key)}`;
    try {
        const ids = foldedIds(view.state);
        if (ids.length) localStorage.setItem(name, JSON.stringify(ids));
        else localStorage.removeItem(name);
    } catch { /* no storage: folds last for this visit only */ }
}

function loadFolds(state, key) {
    if (!state.scope) return [];
    try {
        const ids = JSON.parse(localStorage.getItem(`cc-editor-folds:${placeKey(state, key)}`) ?? '[]');
        return Array.isArray(ids) ? ids : [];
    } catch {
        return [];
    }
}

/**
 * Creates the editor in host. dotnet receives OnEditorChanged(key, text stream) after typing pauses; key
 * names what the text belongs to, so a change still in flight when setText swaps the document
 * lands on the right file. scope (the viewer and the site or template) keys saved folds.
 */
export function create(host, dotnet, text, label, key, canReveal, scope) {
    dispose(host);
    const state = { dotnet, key, scope: scope ?? null, canReveal: !!canReveal, schema: null, silent: false, timer: 0, revealed: [], editable: new Compartment() };
    const view = new EditorView({
        parent: host,
        state: EditorState.create({
            doc: text ?? '',
            extensions: [
                // Ahead of basicSetup, so it outranks the default Enter; completion's Enter still wins.
                keymap.of([{ key: 'Enter', run: yamlNewline }]),
                basicSetup,
                yaml(),
                indentUnit.of('  '),
                EditorState.tabSize.of(2),
                keymap.of([indentWithTab]),
                syntaxHighlighting(highlight),
                theme,
                phrases,
                secretMasks(state),
                literalMasks(state),
                removeButtons(state),
                managedObjects(state),
                policyOrderMarks(state),
                addButtons(state),
                collapseButtons(),
                namedFolds,
                // Diagnostics come from the server's validation via setProblems, never a local linter.
                lintGutter(),
                autocompletion({ override: [ctx => complete(state, ctx)], activateOnTyping: true, icons: false }),
                // A list a click opened on a value already set only offers: Enter is still a line break
                // until an arrow key picks an entry. On an empty value, or opened by typing, Enter accepts.
                Prec.highest(keymap.of([
                    { key: 'ArrowDown', run: () => { state.clickList = false; return false; } },
                    { key: 'ArrowUp', run: () => { state.clickList = false; return false; } },
                    { key: 'Enter', run: v => {
                        if (state.clickList && completionStatus(v.state) === 'active') closeCompletion(v);
                        state.clickList = false;
                        return false;
                    } },
                ])),
                state.editable.of(EditorView.editable.of(true)),
                EditorView.contentAttributes.of({ 'aria-label': label ?? 'YAML', spellcheck: 'false' }),
                EditorView.updateListener.of(u => {
                    if (!state.silent && u.transactions.some(t => t.effects.some(e => e.is(foldEffect) || e.is(unfoldEffect)))) saveFolds(u.view, state);
                    if (u.docChanged) {
                        // Grow with typing at either edge, so an edit there counts as an edit.
                        for (const r of state.revealed) {
                            r.from = u.changes.mapPos(r.from, -1);
                            r.to = u.changes.mapPos(r.to, 1);
                        }
                    }
                    if ((u.selectionSet || u.docChanged) && state.revealed.length) restoreRevealed(u.view, state);
                    if (!u.docChanged && u.state.selection.main.empty && u.transactions.some(t => t.isUserEvent('select.pointer'))
                        && opensOnClick(state, u.state, u.state.selection.main.head)) {
                        state.clickOpen = true;
                        state.clickValue = valueAt(u.state, u.state.selection.main.head);
                        state.clickList = state.clickValue !== '';
                        setTimeout(() => startCompletion(u.view), 0);
                    }
                    // Once that list is open, highlight the value the field holds, where it sits in the list.
                    if (state.clickValue && completionStatus(u.state) === 'active') {
                        const want = state.clickValue.replace(/^["']|["']$/g, '');
                        state.clickValue = '';
                        const at = currentCompletions(u.state).findIndex(o => [o.apply, o.label].some(v => typeof v === 'string' && v.replace(/^["']|["']$/g, '') === want));
                        if (at > 0) setTimeout(() => u.view.dispatch({ effects: setSelectedCompletion(at) }), 0);
                    }
                    if (u.docChanged) { state.clickList = false; state.clickValue = ''; }
                    if (!u.docChanged || state.silent) return;
                    // A firewall policy match key set by the user gets the fields it needs.
                    if (u.transactions.some(t => t.isUserEvent('input') && !t.isUserEvent('input.match-fields'))) {
                        const lines = new Set();
                        u.changes.iterChangedRanges((fa, ta, fb, tb) => {
                            for (let n = u.state.doc.lineAt(fb).number; n <= u.state.doc.lineAt(tb).number; n++) lines.add(n);
                        });
                        for (const n of lines) {
                            if (!matchFieldsChange(u.state.doc, n)) continue;
                            setTimeout(() => {
                                const change = n <= u.view.state.doc.lines ? matchFieldsChange(u.view.state.doc, n) : null;
                                if (change) u.view.dispatch({ ...change, userEvent: 'input.match-fields', scrollIntoView: true });
                            }, 0);
                            break;
                        }
                    }
                    // Deleting a masked secret leaves "key: "; open the menu there too (phones have no Ctrl+Space).
                    const head = u.state.selection.main.head;
                    const lineStart = u.state.doc.lineAt(head).from;
                    if (u.transactions.some(t => t.isUserEvent('delete')) && /:\s$/.test(u.state.sliceDoc(lineStart, head))) {
                        setTimeout(() => startCompletion(u.view), 0);
                    }
                    clearTimeout(state.timer);
                    const key = state.key;
                    state.timer = setTimeout(() => {
                        state.timer = 0;
                        reportChange(view, state, key);
                    }, 250);
                }),
                // A bare "key: " starts value completion without waiting for the next keystroke.
                EditorView.inputHandler.of((v, from, to, insert) => {
                    if (insert === ' ' && v.state.sliceDoc(from - 1, from) === ':') setTimeout(() => startCompletion(v), 0);
                    return false;
                }),
                // A quote typed before text quotes that text, not an empty pair in front of it. Ahead of
                // closeBrackets, which pairs a quote whenever the next character is not a letter ("[TEST]").
                Prec.highest(EditorView.inputHandler.of((v, from, to, insert) => {
                    if ((insert !== '"' && insert !== "'") || from !== to) return false;
                    const end = quotedEnd(v.state.doc.lineAt(from), from);
                    if (end === null) return false;
                    v.dispatch({ changes: [{ from, insert }, { from: end, insert }], selection: { anchor: end + 2 }, userEvent: 'input.type' });
                    return true;
                })),
            ],
        }),
    });
    host._cc = { view, state };
    state.silent = true;
    try { foldIds(view, loadFolds(state, key)); } finally { state.silent = false; }
}

/**
 * Replaces the document without reporting it as an edit. A pending change for the old text is sent
 * first. scope, when given, replaces the editor's (a switch between site config and a template).
 */
export function setText(host, text, key, scope) {
    const cc = host?._cc;
    if (!cc) return;
    if (cc.state.timer) {
        clearTimeout(cc.state.timer);
        cc.state.timer = 0;
        reportChange(cc.view, cc.state, cc.state.key);
    }
    // Each collection keeps its own scroll, cursor, and folds for this visit; one opened for the first
    // time starts at the top, with the folds saved for it.
    const view = cc.view;
    const places = cc.state.places ??= {};
    const folds = [];
    foldedRanges(view.state).between(0, view.state.doc.length, (from, to) => { folds.push({ from, to }); });
    // The top line in view, not a pixel offset: CodeMirror keeps its own scroll across a document swap.
    const top = view.lineBlockAtHeight(view.scrollDOM.getBoundingClientRect().top - view.documentTop).from;
    // Not for the empty text the editor starts with before its first collection loads.
    if (view.state.doc.length > 0) places[placeKey(cc.state, cc.state.key)] = { top, anchor: view.state.selection.main.head, folds };
    if (scope != null) cc.state.scope = scope;
    const place = places[placeKey(cc.state, key)];

    cc.state.key = key;
    cc.state.revealed = [];
    cc.state.silent = true;
    try {
        const length = (text ?? '').length;
        view.dispatch({
            changes: { from: 0, to: view.state.doc.length, insert: text ?? '' },
            selection: { anchor: Math.min(place?.anchor ?? 0, length) },
        });
        view.dispatch(setDiagnostics(view.state, []));
        // A fold saved against other text (an edit since) is dropped rather than misplaced.
        const doc = view.state.doc;
        const valid = (place?.folds ?? []).filter(r => r.to <= doc.length && doc.lineAt(r.from).to === r.from);
        if (place) { if (valid.length) view.dispatch({ effects: valid.map(r => foldEffect.of(r)) }); }
        else foldIds(view, loadFolds(cc.state, key));
    } finally {
        cc.state.silent = false;
    }
    // After CodeMirror's own layout pass, which re-anchors the scroll to the old text's top line.
    const target = Math.min(place?.top ?? 0, view.state.doc.length);
    requestAnimationFrame(() => requestAnimationFrame(() => {
        if (host._cc === cc && cc.state.key === key) view.scrollDOM.scrollTop = target === 0 ? 0 : view.lineBlockAt(target).top;
    }));
}

/**
 * Where a new object goes in the document: the end of the collection's block, with "[]" to drop
 * when the collection was empty. Null when the collection key is not in the document. Pure: the
 * Node harness tests it.
 */
export function insertionPoint(doc, collection) {
    let start = 0;
    for (let n = 1; n <= doc.lines; n++) {
        const m = /^([A-Za-z0-9_]+):\s*(\[\])?\s*$/.exec(doc.line(n).text);
        if (!m) continue;
        if (start) return { at: doc.line(n).from, empty: null };
        if (m[1] !== collection) continue;
        start = n;
        if (m[2]) {
            const line = doc.line(n);
            return { at: line.to, empty: { from: line.from + line.text.indexOf('[]') - 1, to: line.to } };
        }
    }
    return start ? { at: doc.length, empty: null } : null;
}

/** Adds a new object (YAML list item text) to a collection, as an edit, with the cursor on its first blank field. */
export function insertObject(host, collection, text) {
    const cc = host?._cc;
    if (!cc) return;
    const view = cc.view;
    const doc = view.state.doc;
    const point = insertionPoint(doc, collection);
    let item = text.endsWith('\n') ? text : text + '\n';
    let changes;
    let from;
    if (!point) {
        const lead = doc.length && !doc.toString().endsWith('\n') ? '\n' : '';
        from = doc.length + lead.length + collection.length + 2;
        changes = { from: doc.length, insert: `${lead}${collection}:\n${item}` };
    } else if (point.empty) {
        // "wlans: []" becomes "wlans:" and the item; the line's own break follows it.
        from = point.empty.from + 1;
        changes = { from: point.empty.from, to: point.empty.to, insert: '\n' + item.replace(/\n$/, '') };
    } else {
        const needsBreak = point.at > 0 && doc.sliceString(point.at - 1, point.at) !== '\n';
        if (needsBreak) item = '\n' + item;
        from = point.at + (needsBreak ? 1 : 0);
        changes = { from: point.at, insert: item };
    }
    view.dispatch({ changes, userEvent: 'input.add-object', scrollIntoView: true });
    // The first "key: " the object leaves blank, else its first line.
    const after = view.state.doc;
    let cursor = after.lineAt(Math.min(from, after.length)).to;
    for (let pos = from; pos < after.length;) {
        const line = after.lineAt(pos);
        if (pos > from && (/^[A-Za-z0-9_]+:/.test(line.text) || /^- /.test(line.text))) break;
        if (/: $/.test(line.text)) { cursor = line.to; break; }
        pos = line.to + 1;
    }
    view.dispatch({ selection: { anchor: cursor }, scrollIntoView: true });
    view.focus();
}

/** problems: [{ line, level: 'Error'|'Warning'|'Info', message }]. Lines are 1-based. */
export function setProblems(host, problems) {
    const cc = host?._cc;
    if (!cc) return;
    const doc = cc.view.state.doc;
    const diagnostics = (problems ?? [])
        .filter(p => p.line >= 1 && p.line <= doc.lines)
        .map(p => {
            const line = doc.line(p.line);
            const start = line.from + (line.text.length - line.text.trimStart().length);
            return {
                from: start, to: Math.max(start, line.to),
                severity: p.level === 'Error' ? 'error' : p.level === 'Warning' ? 'warning' : 'info',
                message: p.message,
            };
        });
    cc.view.dispatch(setDiagnostics(cc.view.state, diagnostics));
}

/**
 * schema: { collections: { yamlName: Shape }, refs: { refKey: [names] } }
 * Shape: { fields: [{ name, values?, ref?, detail?, shape? }] } in display order; shape nests.
 */
export function setSchema(host, schema) {
    const cc = host?._cc;
    if (!cc) return;
    cc.state.schema = typeof schema === 'string' ? JSON.parse(schema) : schema;
    // The masks depend on which fields are secret; redraw them now the schema says so.
    cc.view.dispatch({ effects: refreshMasks.of(null) });
}

export function dispose(host) {
    const cc = host?._cc;
    if (!cc) return;
    clearTimeout(cc.state.timer);
    cc.view.destroy();
    host._cc = null;
}

// ---------- Completion ----------

const keyPosition = /^(\s*)(- )?([A-Za-z0-9_.-]*)$/;
const flowListPosition = /^(\s*)(- )?([A-Za-z0-9_.-]+):\s*\[([^\]]*)$/;
const valuePosition = /^(\s*)(- )?([A-Za-z0-9_.-]+):\s*([^#\[{]*)$/;
const blockItemPosition = /^(\s*)- ([^#:]*)$/;

// ---------- Firewall policy match fields ----------

// The fields a firewall policy's source or destination needs for its matching_target,
// matching_target_type, and port_matching_type, as the main site's 342 policies carry them
// (Network 11.0.97). A change adds the ones it needs and removes the other types' fields.
const matchOnlyFields = ['ips', 'ip_group_id', 'network_ids', 'match_opposite_networks', 'client_macs', 'macs',
    'web_domains', 'web_matching_type', 'app_ids', 'regions', 'port', 'port_group_id'];

function matchFields(target, targetType, portType) {
    const out = [];
    const typed = def => targetType ? [] : [['matching_target_type', def]];
    switch (target) {
        // An OBJECT (IP group) match carries an ips list too, empty, in every policy seen.
        case 'IP': out.push(...typed('SPECIFIC'), ...((targetType ?? 'SPECIFIC') === 'OBJECT' ? [['ip_group_id', '']] : []), ['ips', '[]']); break;
        case 'NETWORK': out.push(['network_ids', '[]'], ['match_opposite_networks', 'false']); break;
        case 'CLIENT': out.push(['client_macs', '[]']); break;
        case 'MAC': out.push(...typed('SPECIFIC'), ['macs', '[]']); break;
        case 'WEB': out.push(...typed('SPECIFIC'), ['web_domains', '[]'], ['web_matching_type', '']); break;
        case 'APP': out.push(['app_ids', '[]']); break;
        case 'REGION': out.push(['regions', '[]']); break;
    }
    if (portType === 'SPECIFIC') out.push(['port', '']);
    if (portType === 'OBJECT') out.push(['port_group_id', '']);
    return out;
}

const matchKeyLine = /^(\s*)(?:- )?(matching_target|matching_target_type|port_matching_type):\s*([A-Z_]+)\s*$/;

/** After a match key's value is set in firewall_policies, the change that adds the fields it needs; null when none. */
export function matchFieldsChange(doc, lineNo) {
    const line = doc.line(lineNo);
    const m = matchKeyLine.exec(line.text);
    if (!m) return null;
    const indent = m[1].length + (line.text.slice(m[1].length).startsWith('- ') ? 2 : 0);
    let top = null;
    for (let n = lineNo - 1; n >= 1 && top === null; n--) {
        const t = /^([A-Za-z0-9_]+):\s*$/.exec(doc.line(n).text);
        if (t) top = t[1];
    }
    if (top !== 'firewall_policies') return null;
    // The mapping the line sits in: its keys at the same indent, each with the lines it spans (a
    // block list runs on below its key), up to a less-indented line.
    const keyAt = n => {
        const t = doc.line(n).text;
        const lead = t.length - t.trimStart().length + (t.trimStart().startsWith('- ') ? 2 : 0);
        const k = /^\s*(?:- )?([A-Za-z0-9_]+):\s*(.*)$/.exec(t);
        return { lead, key: k?.[1], value: (k?.[2] ?? '').trim(), item: t.trimStart().startsWith('- '), blank: t.trim() === '' };
    };
    let start = lineNo;
    // Up to the mapping's first key: an item at its own indent ("- key:") starts it.
    for (let n = lineNo - 1; n >= 1 && !(keyAt(start).item && keyAt(start).lead === indent); n--) {
        const k = keyAt(n);
        if (k.blank) continue;
        if (k.lead < indent) break;
        start = n;
    }
    const siblings = new Map();
    let current = null;
    for (let n = start; n <= doc.lines; n++) {
        const k = keyAt(n);
        if (k.blank) continue;
        if (n > start && (k.lead < indent || (k.item && k.lead <= indent))) break;
        if (k.lead === indent && k.key) siblings.set(k.key, current = { value: k.value, first: n, last: n });
        else if (current) current.last = n;
    }
    const value = key => siblings.get(key)?.value || undefined;
    const needed = matchFields(value('matching_target'), value('matching_target_type'), value('port_matching_type'));
    const missing = needed.filter(([k]) => !siblings.has(k));
    // UniFi keeps no SPECIFIC match type on an ANY match (PlanBuilder.DropAnyMatchType).
    const dropType = value('matching_target') === 'ANY' && value('matching_target_type') === 'SPECIFIC' ? ['matching_target_type'] : [];
    const unused = [...matchOnlyFields, ...dropType].filter(k => siblings.has(k) && !needed.some(([n]) => n === k))
        .map(k => siblings.get(k)).filter(s => s.first !== lineNo);
    if (missing.length === 0 && unused.length === 0) return null;

    // Each unused key goes with its lines and the line break before them.
    const changes = unused.sort((a, b) => a.first - b.first)
        .map(s => ({ from: doc.line(s.first).from - 1, to: doc.line(s.last).to }));
    let selection;
    if (missing.length > 0) {
        const pad = ' '.repeat(indent);
        changes.push({ from: line.to, insert: missing.map(([k, v]) => `\n${pad}${k}: ${v}`).join('') });
        changes.sort((a, b) => a.from - b.from);
        // The cursor goes into the first added field left to fill (inside its [], or after "key: "),
        // past any removal above it.
        const removedAbove = changes.filter(c => c.to !== undefined && c.to <= line.to).reduce((sum, c) => sum + (c.to - c.from), 0);
        const target = Math.max(0, missing.findIndex(([, v]) => v === '' || v === '[]'));
        const upTo = missing.slice(0, target + 1).map(([k, v], i) => `\n${pad}${k}: ${i === target ? '' : v}`).join('');
        selection = { anchor: line.to - removedAbove + upTo.length + (missing[target][1] === '[]' ? 1 : 0) };
    }
    return selection ? { changes, selection } : { changes };
}

/**
 * Where a quote typed at pos closes when text follows it: the end of that value (before a list's
 * comma or bracket, a trailing comment, or trailing spaces). Null when nothing follows, or it is
 * already a quote or a closing bracket: then the usual pairing applies. Pure, exported for testing.
 */
export function quotedEnd(line, pos) {
    const rest = line.text.slice(pos - line.from);
    if (!rest || /^[\s"'\]},]/.test(rest)) return null;
    const inList = /[[{]/.test(line.text.slice(0, pos - line.from));
    const stop = rest.search(inList ? /,|\]|\}|\s#/ : /\s#/);
    const value = (stop < 0 ? rest : rest.slice(0, stop)).replace(/\s+$/, '');
    return value ? pos + value.length : null;
}

/** The value on pos's "key: value" line, without a trailing comment; '' when none. */
function valueAt(es, pos) {
    const text = es.doc.lineAt(pos).text;
    const colon = text.indexOf(':');
    if (colon < 0) return '';
    const rest = text.slice(colon + 1);
    const comment = rest.search(/\s#/);
    return (comment >= 0 ? rest.slice(0, comment) : rest).trim();
}

// A click into a single value whose field has choices (a zone, a network) opens them without typing.
function opensOnClick(state, es, pos) {
    if (!state.schema) return false;
    // A click right of a folded object lands at its fold's end, the last line it hides: not a value the reader sees.
    let hidden = false;
    foldedRanges(es).between(pos, pos, (from, to) => { if (pos > from && pos <= to) hidden = true; });
    if (hidden) return false;
    const line = es.doc.lineAt(pos);
    const v = valuePosition.exec(line.text.slice(0, pos - line.from));
    if (!v) return false;
    const field = fieldAt(state.schema, es.doc, line.number, v[1].length + (v[2] ? 2 : 0), v[3]);
    return !!field && !field.secret && valueResult(state.schema, field, pos, new Set()) !== null;
}

function complete(state, ctx) {
    const schema = state.schema;
    if (!schema) return null;
    const doc = ctx.state.doc;
    const line = doc.lineAt(ctx.pos);
    const before = line.text.slice(0, ctx.pos - line.from);

    // key: [a, b, |  - one list item, skipping the ones already listed.
    const f = flowListPosition.exec(before);
    if (f) {
        const field = fieldAt(schema, doc, line.number, f[1].length + (f[2] ? 2 : 0), f[3]);
        const parts = f[4].split(',');
        const typed = parts.pop().trimStart();
        const listed = new Set(parts.map(p => p.trim()));
        return valueResult(schema, field, ctx.pos - typed.length, listed);
    }

    // key: |  - a single value.
    const v = valuePosition.exec(before);
    if (v) {
        const field = fieldAt(schema, doc, line.number, v[1].length + (v[2] ? 2 : 0), v[3]);
        const typed = v[4] ?? '';
        if (field?.secret) return generatorResult(state, ctx.pos - typed.length);
        const result = valueResult(schema, field, ctx.pos - typed.length, new Set());
        // Opened by a click: every choice, and picking one replaces the whole value.
        if (result && state.clickOpen) {
            state.clickOpen = false;
            const rest = line.text.slice(ctx.pos - line.from);
            const comment = rest.search(/\s#/);
            return { ...result, to: comment >= 0 ? ctx.pos + comment : line.to, filter: false };
        }
        return result;
    }

    // key:
    //   - a
    //   - |  - a list item on its own line, under the nearest less-indented key.
    const b = blockItemPosition.exec(before);
    if (b) {
        const parent = parentKeyLine(doc, line.number, b[1].length);
        if (parent) {
            const field = fieldAt(schema, doc, parent.number, parent.indent, parent.key);
            if (field && !field.shape) {
                return valueResult(schema, field, ctx.pos - b[2].length, blockItems(doc, parent.number, b[1].length, line.number));
            }
        }
    }

    const k = keyPosition.exec(before);
    if (k && (ctx.explicit || k[3].length > 0)) {
        const indent = k[1].length + (k[2] ? 2 : 0);
        if (indent === 0) return null;
        const shape = shapeAt(schema, ctx.state.doc, line.number, indent);
        if (!shape?.fields) return null;
        const present = siblingKeys(ctx.state.doc, line.number, indent);
        const options = shape.fields
            .filter(f => !present.has(f.name))
            .map((f, i) => ({
                label: f.name,
                detail: f.detail ?? '',
                type: 'property',
                boost: -i / 1000,
                apply: f.shape ? `${f.name}:\n${' '.repeat(indent + 2)}` : `${f.name}: `,
            }));
        return { from: ctx.pos - k[3].length, options, validFor: /^[A-Za-z0-9_.-]*$/ };
    }
    return null;
}

function fieldAt(schema, doc, lineNo, indent, key) {
    return shapeAt(schema, doc, lineNo, indent)?.fields?.find(f => f.name === key) ?? null;
}

// Candidates are plain values (enums) or { label, value } pairs (references): the label is what
// the user knows the object by, the value is the YAML the converter writes for it, which can be
// a name, {vlan: 30}, {mac: ...}, or {id: ...} when names collide.
// A secret field offers new values from the server's generators (CSPRNG; EFF long wordlist).
function generatorResult(state, from) {
    const generate = kind => (view, completion, start) => {
        state.dotnet.invokeMethodAsync('GenerateSecret', kind)
            .then(value => {
                // Replace the whole value, a masked token included, but not a trailing comment.
                const line = view.state.doc.lineAt(start);
                const rest = line.text.slice(start - line.from);
                const comment = rest.search(/\s#/);
                const end = comment >= 0 ? start + comment : line.to;
                view.dispatch({ changes: { from: start, to: end, insert: quoteIfNeeded(value) } });
            })
            .catch(e => console.warn('cc-editor: generate failed', e));
    };
    return {
        from,
        filter: false,
        options: [
            { label: 'Generate passphrase', detail: '5 words, ~65 bits', type: 'keyword', apply: generate('Passphrase') },
            { label: 'Generate random', detail: '16 letters and digits, ~93 bits', type: 'keyword', apply: generate('Random') },
        ],
    };
}

function valueResult(schema, field, from, exclude) {
    if (!field) return null;
    const candidates = field.ref ? schema.refs?.[field.ref] ?? [] : field.values ?? [];
    const options = candidates
        .map(c => typeof c === 'object' && c !== null
            ? { label: c.label, detail: c.detail ?? (c.value !== c.label ? c.value : ''), apply: c.value, type: 'variable' }
            : { label: String(c), apply: quoteIfNeeded(String(c)), type: 'enum' })
        .filter(o => !exclude.has(o.apply) && !exclude.has(o.label));
    return options.length === 0 ? null : { from, options, validFor: /^[^,\]#]*$/ };
}

function parentKeyLine(doc, lineNo, itemIndent) {
    for (let n = lineNo - 1; n >= 1; n--) {
        const text = doc.line(n).text;
        if (!text.trim() || text.trimStart().startsWith('#')) continue;
        const lead = text.length - text.trimStart().length;
        if (lead >= itemIndent) continue;
        const m = /^(\s*)(- )?([A-Za-z0-9_.-]+):\s*$/.exec(text);
        return m ? { number: n, key: m[3], indent: m[1].length + (m[2] ? 2 : 0) } : null;
    }
    return null;
}

// The values already listed under a block list, so completion does not offer them twice.
function blockItems(doc, parentLine, itemIndent, skipLine) {
    const items = new Set();
    for (let n = parentLine + 1; n <= doc.lines; n++) {
        const text = doc.line(n).text;
        if (!text.trim()) continue;
        const lead = text.length - text.trimStart().length;
        if (lead < itemIndent) break;
        const m = /^\s*- (.+)$/.exec(text);
        if (m && lead === itemIndent && n !== skipLine) items.add(m[1].trim());
    }
    return items;
}

function quoteIfNeeded(s) {
    const plain = /^[A-Za-z0-9_./@][A-Za-z0-9 _./@:+()-]*$/.test(s) && !s.endsWith(' ') && !s.includes(': ');
    const ambiguous = /^(null|yes|no|on|off|~|[-+]?[0-9][0-9_]*(\.[0-9]*)?([eE][-+]?[0-9]+)?|0x[0-9a-fA-F]+)$/i.test(s);
    // true/false stay bare: they are the field's real boolean values.
    return plain && !ambiguous ? s : JSON.stringify(s);
}

// The schema shape for keys written at `indent` on line `lineNo`: walks up to the collection
// key at column 0, then down through each enclosing mapping key.
function shapeAt(schema, doc, lineNo, indent) {
    const parents = [];
    let want = indent;
    let collection = null;
    for (let n = lineNo - 1; n >= 1; n--) {
        const text = doc.line(n).text;
        if (!text.trim() || text.trimStart().startsWith('#')) continue;
        const m = /^(\s*)(- )?([A-Za-z0-9_.-]+):/.exec(text);
        if (!m) continue;
        const keyIndent = m[1].length + (m[2] ? 2 : 0);
        if (keyIndent === 0) { collection = m[3]; break; }
        // A list item's first key ("- name:") sits at its siblings' indent, so it never matches here.
        if (keyIndent < want) {
            parents.unshift(m[3]);
            want = keyIndent;
        }
    }
    let shape = collection ? schema.collections?.[collection] : null;
    for (const key of parents) {
        shape = shape?.fields?.find(f => f.name === key)?.shape;
    }
    return shape ?? null;
}

// Keys already set on the object the cursor is in, so completion does not offer them twice.
function siblingKeys(doc, lineNo, indent) {
    const keys = new Set();
    const scan = (from, step) => {
        for (let n = from; n >= 1 && n <= doc.lines; n += step) {
            const text = doc.line(n).text;
            if (!text.trim()) continue;
            const m = /^(\s*)(- )?([A-Za-z0-9_.-]+):/.exec(text);
            const lead = text.length - text.trimStart().length;
            if (!m) { if (lead < indent) return; continue; }
            const keyIndent = m[1].length + (m[2] ? 2 : 0);
            if (keyIndent < indent) return;
            if (keyIndent === indent) {
                // A list marker at this level starts an object: scanning down it is the next
                // object (not ours); scanning up it is our object's first key.
                if (m[2] && step > 0) return;
                keys.add(m[3]);
                if (m[2]) return;
            }
        }
    };
    const own = /^(\s*)(- )/.exec(doc.line(lineNo).text);
    if (!own) scan(lineNo - 1, -1);
    scan(lineNo + 1, 1);
    return keys;
}
