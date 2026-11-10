# Fetches NativeForms at the commit pinned in build/NativeForms.ref into ..\NativeForms, where
# build/NativeForms.props picks it up - the same checkout CI makes (.github/actions/nativeforms-source).
# Needed because Compression.NativeUI uses NativeForms types that no published package carries yet.
# Usage: build/get-nativeforms.ps1 [-Directory <target-directory>]
param([string]$Directory)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$ref = (Get-Content (Join-Path $root 'build/NativeForms.ref') -Raw).Trim()
if (-not $Directory) { $Directory = Join-Path (Split-Path -Parent $root) 'NativeForms' }
if (-not (Test-Path (Join-Path $Directory '.git'))) { git init -q $Directory; if ($LASTEXITCODE) { exit $LASTEXITCODE } }
git -C $Directory fetch -q --depth 1 https://github.com/Hawkynt/NativeForms.git $ref
if ($LASTEXITCODE) { exit $LASTEXITCODE }
git -C $Directory checkout -q --detach FETCH_HEAD
if ($LASTEXITCODE) { exit $LASTEXITCODE }
Write-Host "NativeForms $(git -C $Directory log -1 --format='%h %s') in $Directory"
if ($PSBoundParameters.ContainsKey('Directory')) {
  Write-Host "Build with -p:NativeFormsSource=`"$((Resolve-Path $Directory).Path)`" (or set NativeFormsSource) to use this location."
}
