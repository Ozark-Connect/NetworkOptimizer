# Rebuilds the vendored CodeMirror 6 bundle used by Change Control's YAML editor.
# Output: src/NetworkOptimizer.Web/wwwroot/lib/codemirror/codemirror.bundle.js (+ LICENSE).
# Needs Node and npm. Installs from scripts/codemirror-bundle/package-lock.json (every transitive
# version pinned) with install scripts off, in a temp folder; nothing is installed into the repo.
# After a rebuild, bump the ?v= on the bundle import in wwwroot/js/cc-editor.js.
param([switch]$UpdateLock)
$ErrorActionPreference = 'Stop'

$repo = Split-Path -Parent $PSScriptRoot
$pkgDir = Join-Path $PSScriptRoot 'codemirror-bundle'
$out = Join-Path $repo 'src/NetworkOptimizer.Web/wwwroot/lib/codemirror'
$work = Join-Path ([System.IO.Path]::GetTempPath()) "cm-bundle-$([guid]::NewGuid().ToString('N'))"
New-Item -ItemType Directory -Force $work, $out | Out-Null

Push-Location $work
try {
    Copy-Item (Join-Path $pkgDir 'package.json'), (Join-Path $pkgDir 'entry.js') .
    if ($UpdateLock -or -not (Test-Path (Join-Path $pkgDir 'package-lock.json'))) {
        npm install --package-lock-only --ignore-scripts --no-audit --no-fund | Out-Null
        Copy-Item package-lock.json (Join-Path $pkgDir 'package-lock.json')
    }
    else {
        Copy-Item (Join-Path $pkgDir 'package-lock.json') .
    }
    npm ci --ignore-scripts --no-audit --no-fund | Out-Null
    # esbuild's binary comes from an optional platform package, which ci installs without scripts.
    npx --no-install esbuild entry.js --bundle --format=esm --minify --legal-comments=eof --outfile=bundle.js
    $pkg = Get-Content package.json -Raw | ConvertFrom-Json
    $versions = ($pkg.dependencies.PSObject.Properties | Where-Object Name -ne 'esbuild' | ForEach-Object { "$($_.Name)@$($_.Value)" }) -join ', '
    $header = "/* CodeMirror 6 bundle for Change Control (MIT; licenses at the end and in LICENSE). Built by scripts/build-codemirror-bundle.ps1 from: $versions */`n"
    Set-Content -Path (Join-Path $out 'codemirror.bundle.js') -Value ($header + (Get-Content bundle.js -Raw)) -Encoding utf8 -NoNewline
    Copy-Item node_modules/codemirror/LICENSE (Join-Path $out 'LICENSE')
}
finally {
    Pop-Location
    Remove-Item -Recurse -Force $work
}
Get-Item (Join-Path $out 'codemirror.bundle.js') | Select-Object Name, Length
