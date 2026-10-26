// What cc-editor.js imports from the bundle. Add an export here, then rebuild.
export { EditorView, keymap, hoverTooltip, Decoration, WidgetType, ViewPlugin, MatchDecorator } from '@codemirror/view';
export { EditorState, Compartment, StateEffect } from '@codemirror/state';
export { basicSetup } from 'codemirror';
export { indentWithTab } from '@codemirror/commands';
export { indentUnit, syntaxHighlighting, HighlightStyle, syntaxTree } from '@codemirror/language';
export { autocompletion, completionKeymap, startCompletion } from '@codemirror/autocomplete';
export { linter, setDiagnostics, lintGutter } from '@codemirror/lint';
export { yaml } from '@codemirror/lang-yaml';
export { tags } from '@lezer/highlight';
