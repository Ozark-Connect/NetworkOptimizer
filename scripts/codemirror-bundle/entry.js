// What cc-editor.js imports from the bundle. Add an export here, then rebuild.
export { EditorView, keymap, hoverTooltip, Decoration, WidgetType, ViewPlugin, MatchDecorator, gutter, GutterMarker } from '@codemirror/view';
export { EditorState, Compartment, StateEffect, StateField, Prec, RangeSetBuilder, Text } from '@codemirror/state';
export { basicSetup } from 'codemirror';
export { indentWithTab } from '@codemirror/commands';
export { indentUnit, syntaxHighlighting, HighlightStyle, syntaxTree, codeFolding, foldEffect, unfoldEffect, foldedRanges } from '@codemirror/language';
export { autocompletion, completionKeymap, startCompletion, closeCompletion, completionStatus, currentCompletions, setSelectedCompletion } from '@codemirror/autocomplete';
export { linter, setDiagnostics, lintGutter } from '@codemirror/lint';
export { openSearchPanel } from '@codemirror/search';
export { Chunk } from '@codemirror/merge';
export { yaml } from '@codemirror/lang-yaml';
export { tags } from '@lezer/highlight';
