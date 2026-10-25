// Change Control's YAML editor: CodeMirror 6 with completion from the site's own config and
// diagnostics from the server's validation. One editor per host element, kept on host._cc.
import {
    EditorView, EditorState, Compartment, basicSetup, keymap, indentWithTab, indentUnit, yaml,
    autocompletion, startCompletion, setDiagnostics, lintGutter, syntaxHighlighting, HighlightStyle, tags,
} from '../lib/codemirror/codemirror.bundle.js?v=1';

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
    '.cm-activeLine': { backgroundColor: 'rgba(255,255,255,0.03)' },
    '.cm-activeLineGutter': { backgroundColor: 'transparent', color: 'var(--text-secondary)' },
    '&.cm-focused': { outline: 'none' },
    '&.cm-focused .cm-cursor': { borderLeftColor: 'var(--text-primary)' },
    '.cm-selectionBackground, &.cm-focused .cm-selectionBackground, ::selection': { backgroundColor: 'rgba(71,151,255,0.25)' },
    '.cm-tooltip': { backgroundColor: 'var(--bg-tertiary)', border: '1px solid var(--bg-hover)', color: 'var(--text-primary)' },
    '.cm-tooltip-autocomplete > ul > li[aria-selected]': { backgroundColor: 'var(--primary-color)', color: '#fff' },
    '.cm-completionDetail': { color: 'var(--text-secondary)', fontStyle: 'normal', marginLeft: '0.75rem' },
    '.cm-diagnostic-error': { borderLeftColor: 'var(--danger-color)' },
    '.cm-diagnostic-warning': { borderLeftColor: 'var(--warning-color)' },
    '.cm-diagnostic-info': { borderLeftColor: 'var(--info-color)' },
}, { dark: true });

/**
 * Creates the editor in host. dotnet receives OnEditorChanged(key, text) after typing pauses; key
 * names what the text belongs to, so a change still in flight when setText swaps the document
 * lands on the right file.
 */
export function create(host, dotnet, text, label, key) {
    dispose(host);
    const state = { dotnet, key, schema: null, silent: false, timer: 0, editable: new Compartment() };
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
                // Diagnostics come from the server's validation via setProblems, never a local linter.
                lintGutter(),
                autocompletion({ override: [ctx => complete(state, ctx)], activateOnTyping: true, icons: false }),
                state.editable.of(EditorView.editable.of(true)),
                EditorView.contentAttributes.of({ 'aria-label': label ?? 'YAML', spellcheck: 'false' }),
                EditorView.updateListener.of(u => {
                    if (!u.docChanged || state.silent) return;
                    clearTimeout(state.timer);
                    const key = state.key;
                    state.timer = setTimeout(() => {
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
    if (host?._cc) host._cc.state.schema = typeof schema === 'string' ? JSON.parse(schema) : schema;
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
        if (!text.trim()) continue;
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
