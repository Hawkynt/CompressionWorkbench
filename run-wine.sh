#!/usr/bin/env bash
# Run the CompressionWorkbench UI under Wine on Linux.
# First run builds a self-contained Windows executable; subsequent runs reuse it.
# Re-run with --rebuild to force a fresh publish.
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
EXE="$SCRIPT_DIR/Compression.UI/bin/wine-publish/Compression.UI.exe"

# Always use the user's own Wine prefix so existing drive mappings are inherited.
export WINEPREFIX="$HOME/.wine"

# One-time (idempotent) Wine configuration for this app.
# Guarded by a sentinel file so it only re-runs after --rebuild.
_wine_setup() {
  local sentinel="$WINEPREFIX/.cwb-setup-done"
  [[ -f "$sentinel" ]] && return

  echo "Configuring Wine prefix (one-time setup)..."

  # Fix black menu boxes: tell WPF to skip hardware acceleration entirely.
  # renderer=gdi avoids the D3D/GL stack for window drawing.
  wine reg add "HKLM\\SOFTWARE\\Microsoft\\Avalon.Graphics" \
    /v DisableHWAcceleration /t REG_DWORD /d 1 /f >/dev/null 2>&1
  wine reg add "HKCU\\Software\\Wine\\Direct3D" \
    /v renderer /t REG_SZ /d gdi /f >/dev/null 2>&1

  # Map H: to the Linux home directory so file dialogs can reach it directly.
  wine reg add "HKCU\\Software\\Wine\\Drives" \
    /v "H:" /t REG_SZ /d "$HOME" /f >/dev/null 2>&1

  # Ensure the Wine Documents folder points to the real XDG documents directory.
  local wine_docs="$WINEPREFIX/drive_c/users/$USER/Documents"
  local linux_docs
  linux_docs="$(xdg-user-dir DOCUMENTS 2>/dev/null || echo "$HOME/Documents")"
  if [[ -d "$linux_docs" && ! -L "$wine_docs" ]]; then
    rm -rf "$wine_docs"
    ln -s "$linux_docs" "$wine_docs"
  fi

  # Symlink all system TTF fonts into Wine so WPF can resolve font metrics.
  local wine_fonts="$WINEPREFIX/drive_c/windows/Fonts"
  local linked=0
  while IFS= read -r -d '' font; do
    local name
    name="$(basename "$font")"
    if [[ ! -e "$wine_fonts/$name" ]]; then
      ln -s "$font" "$wine_fonts/$name"
      (( ++linked ))
    fi
  done < <(find /usr/share/fonts -name "*.ttf" -o -name "*.TTF" -print0 2>/dev/null)
  echo "  Linked $linked font(s) into Wine prefix."

  # Substitute WPF's default Windows face names with available Linux equivalents.
  # Without these, WPF calls FailFast when it can't resolve the font metrics.
  local sub="HKCU\\Software\\Wine\\Fonts\\Replacements"
  wine reg add "$sub" /v "Segoe UI"         /t REG_SZ /d "DejaVu Sans"        /f >/dev/null 2>&1
  wine reg add "$sub" /v "Arial"            /t REG_SZ /d "Liberation Sans"    /f >/dev/null 2>&1
  wine reg add "$sub" /v "Times New Roman"  /t REG_SZ /d "Liberation Serif"   /f >/dev/null 2>&1
  wine reg add "$sub" /v "Courier New"      /t REG_SZ /d "Liberation Mono"    /f >/dev/null 2>&1
  wine reg add "$sub" /v "Tahoma"           /t REG_SZ /d "DejaVu Sans"        /f >/dev/null 2>&1
  wine reg add "$sub" /v "Calibri"          /t REG_SZ /d "DejaVu Sans"        /f >/dev/null 2>&1
  wine reg add "$sub" /v "Consolas"         /t REG_SZ /d "DejaVu Sans Mono"   /f >/dev/null 2>&1

  touch "$sentinel"
  echo "Wine setup complete."
}

if [[ "${1-}" == "--rebuild" ]]; then
  shift
  rm -f "$EXE" "$WINEPREFIX/.cwb-setup-done"
fi

if [[ ! -f "$EXE" ]]; then
  echo "Publishing self-contained Windows executable (first run takes a minute)..."
  dotnet publish "$SCRIPT_DIR/Compression.UI/Compression.UI.csproj" \
    -c Release -r win-x64 \
    --self-contained true \
    -p:PublishSingleFile=true \
    -p:IncludeNativeLibrariesForSelfExtract=true \
    -o "$(dirname "$EXE")"
fi

_wine_setup

echo "Starting CompressionWorkbench UI via Wine..."
WINEDEBUG=-all COMPRESSIONWORKBENCH_WINE=1 exec wine "$EXE" "$@"
