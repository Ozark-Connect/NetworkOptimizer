# Rebuilds the vendored CodeMirror 6 bundle used by Change Control's YAML editor.
# Output: src/NetworkOptimizer.Web/wwwroot/lib/codemirror/codemirror.bundle.js (+ LICENSE).
# Needs Node and npm. Nothing is installed into the repo: the build runs in a temp folder.
$ErrorActionPreference = 'Stop'

$repo = Split-Path -Parent $PSScriptRoot
$out = Join-Path $repo 'src/NetworkOptimizer.Web/wwwroot/lib/codemirror'
$work = Join-Path ([System.IO.Path]::GetTempPath()) "cm-bundle-$([guid]::NewGuid().ToString('N'))"
New-Item -ItemType Directory -Force $work, $out | Out-Null

$packages = @(
    'codemirror@6.0.1', '@codemirror/state@6.5.2', '@codemirror/view@6.38.1', '@codemirror/language@6.11.2',
    '@codemirror/commands@6.8.1', '@codemirror/autocomplete@6.18.6', '@codemirror/lint@6.8.5',
    '@codemirror/search@6.5.11', '@codemirror/lang-yaml@6.1.2', '@lezer/highlight@1.2.1', 'esbuild@0.25.8'
)

$entry = @'
export { EditorView, keymap, hoverTooltip } from '@codemirror/view';
export { EditorState, Compartment, StateEffect } from '@codemirror/state';
export { basicSetup } from 'codemirror';
export { indentWithTab } from '@codemirror/commands';
export { indentUnit, syntaxHighlighting, HighlightStyle, syntaxTree } from '@codemirror/language';
export { autocompletion, completionKeymap, startCompletion } from '@codemirror/autocomplete';
export { linter, setDiagnostics, lintGutter } from '@codemirror/lint';
export { yaml } from '@codemirror/lang-yaml';
export { tags } from '@lezer/highlight';
'@

Push-Location $work
try {
    npm init -y | Out-Null
    npm install --no-audit --no-fund --save-exact @packages | Out-Null
    Set-Content -Path entry.js -Value $entry -Encoding utf8
    npx esbuild entry.js --bundle --format=esm --minify --legal-comments=none --outfile=bundle.js
    $versions = ($packages | Where-Object { $_ -notlike 'esbuild@*' }) -join ', '
    $header = "/* CodeMirror 6 bundle for Change Control (MIT, see LICENSE). Built by scripts/build-codemirror-bundle.ps1 from: $versions */`n"
    Set-Content -Path (Join-Path $out 'codemirror.bundle.js') -Value ($header + (Get-Content bundle.js -Raw)) -Encoding utf8 -NoNewline
    Copy-Item node_modules/codemirror/LICENSE (Join-Path $out 'LICENSE')
}
finally {
    Pop-Location
    Remove-Item -Recurse -Force $work
}
Get-Item (Join-Path $out 'codemirror.bundle.js') | Select-Object Name, Length
