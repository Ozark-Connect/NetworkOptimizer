// Change Control's YAML editor: CodeMirror 6 with completion from the site's own config and
// diagnostics from the server's validation. One editor per host element, kept on host._cc.
import {
    EditorView, EditorState, Compartment, basicSetup, keymap, indentWithTab, indentUnit, yaml,
    autocompletion, startCompletion, setDiagnostics, lintGutter, syntaxHighlighting, HighlightStyle, tags,
    Decoration, WidgetType, ViewPlugin, MatchDecorator, StateEffect,
} from '../lib/codemirror/codemirror.bundle.js?v=3';

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
    '.cc-secret button': {
        font: 'inherit', fontSize: '0.75rem', letterSpacing: 'normal', padding: '0 0.35rem', cursor: 'pointer',
        border: '1px solid var(--bg-hover)', borderRadius: '3px', background: 'transparent', color: 'var(--info-color)',
    },
}, { dark: true });

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

/**
 * Creates the editor in host. dotnet receives OnEditorChanged(key, text) after typing pauses; key
 * names what the text belongs to, so a change still in flight when setText swaps the document
 * lands on the right file.
 */
export function create(host, dotnet, text, label, key, canReveal) {
    dispose(host);
    const state = { dotnet, key, canReveal: !!canReveal, schema: null, silent: false, timer: 0, revealed: [], editable: new Compartment() };
    const view = new EditorView({
        parent: host,
        state: EditorState.create({
            doc: text ?? '',
            extensions: [
                basicSetup,
                yaml(),
                indentUnit.of('  '),
                EditorState.tabSize.of(2),
                keymap.of([indentWithTab]),
                syntaxHighlighting(highlight),
                theme,
                secretMasks(state),
                literalMasks(state),
                // Diagnostics come from the server's validation via setProblems, never a local linter.
                lintGutter(),
                autocompletion({ override: [ctx => complete(state, ctx)], activateOnTyping: true, icons: false }),
                state.editable.of(EditorView.editable.of(true)),
                EditorView.contentAttributes.of({ 'aria-label': label ?? 'YAML', spellcheck: 'false' }),
                EditorView.updateListener.of(u => {
                    if (u.docChanged) {
                        // Grow with typing at either edge, so an edit there counts as an edit.
                        for (const r of state.revealed) {
                            r.from = u.changes.mapPos(r.from, -1);
                            r.to = u.changes.mapPos(r.to, 1);
                        }
                    }
                    if ((u.selectionSet || u.docChanged) && state.revealed.length) restoreRevealed(u.view, state);
                    if (!u.docChanged || state.silent) return;
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
                        state.dotnet.invokeMethodAsync('OnEditorChanged', key, view.state.doc.toString())
                            .catch(e => console.warn('cc-editor: change callback failed', e));
                    }, 250);
                }),
                // A bare "key: " starts value completion without waiting for the next keystroke.
                EditorView.inputHandler.of((v, from, to, insert) => {
                    if (insert === ' ' && v.state.sliceDoc(from - 1, from) === ':') setTimeout(() => startCompletion(v), 0);
                    return false;
                }),
            ],
        }),
    });
    host._cc = { view, state };
}

/** Replaces the document without reporting it as an edit. A pending change for the old text is sent first. */
export function setText(host, text, key) {
    const cc = host?._cc;
    if (!cc) return;
    if (cc.state.timer) {
        clearTimeout(cc.state.timer);
        cc.state.timer = 0;
        cc.state.dotnet.invokeMethodAsync('OnEditorChanged', cc.state.key, cc.view.state.doc.toString())
            .catch(e => console.warn('cc-editor: change callback failed', e));
    }
    cc.state.key = key;
    cc.state.revealed = [];
    cc.state.silent = true;
    try {
        cc.view.dispatch({ changes: { from: 0, to: cc.view.state.doc.length, insert: text ?? '' } });
        cc.view.dispatch(setDiagnostics(cc.view.state, []));
    } finally {
        cc.state.silent = false;
    }
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
        return valueResult(schema, field, ctx.pos - typed.length, new Set());
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
            { label: 'Generate passphrase', detail: '6 words, ~77 bits', type: 'keyword', apply: generate('Passphrase') },
            { label: 'Generate random', detail: '24 letters and digits, ~140 bits', type: 'keyword', apply: generate('Random') },
        ],
    };
}

function valueResult(schema, field, from, exclude) {
    if (!field) return null;
    const candidates = field.ref ? schema.refs?.[field.ref] ?? [] : field.values ?? [];
    const options = candidates
        .map(c => typeof c === 'object' && c !== null
            ? { label: c.label, detail: c.value !== c.label ? c.value : '', apply: c.value, type: 'variable' }
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
