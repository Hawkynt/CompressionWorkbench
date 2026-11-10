#!/usr/bin/env bash
# Fetches NativeForms at the commit pinned in build/NativeForms.ref into ../NativeForms, where
# build/NativeForms.props picks it up - the same checkout CI makes (.github/actions/nativeforms-source).
# Needed because Compression.NativeUI uses NativeForms types that no published package carries yet.
# Usage: build/get-nativeforms.sh [target-directory]
set -euo pipefail
root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
ref="$(tr -d '[:space:]' < "$root/build/NativeForms.ref")"
dir="${1:-$root/../NativeForms}"
if [ ! -d "$dir/.git" ]; then
  git init -q "$dir"
fi
git -C "$dir" fetch -q --depth 1 https://github.com/Hawkynt/NativeForms.git "$ref"
git -C "$dir" checkout -q --detach FETCH_HEAD
echo "NativeForms $(git -C "$dir" log -1 --format='%h %s') in $dir"
if [ "$#" -gt 0 ]; then
  echo "Build with -p:NativeFormsSource=\"$(cd "$dir" && pwd)\" (or set NativeFormsSource) to use this location."
fi
