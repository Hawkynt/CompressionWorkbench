#!/usr/bin/env bash
#
# setup-test-environment.sh
#
# Installs the third-party filesystem and archive tools that the test suite
# uses to validate our own readers/writers against real OS implementations
# (mtools, e2fsprogs, ntfs-3g, xfsprogs, btrfs-progs, mksquashfs, xorriso, ...).
#
# Tests that need a tool which is not installed skip themselves gracefully, so
# this script is optional — but running it unlocks the external-validation
# tests for the widest possible coverage.
#
# Usage:   ./setup-test-environment.sh [--dry-run] [--yes]
# Detects apt / dnf / pacman / zypper and maps each logical tool to that
# distribution's package name. Unavailable packages are skipped with a note
# rather than aborting the run.

set -u

DRY_RUN=0
ASSUME_YES=0
for arg in "$@"; do
  case "$arg" in
    --dry-run) DRY_RUN=1 ;;
    --yes|-y)  ASSUME_YES=1 ;;
    -h|--help)
      grep '^#' "$0" | sed 's/^# \{0,1\}//'
      exit 0 ;;
    *) echo "unknown argument: $arg" >&2; exit 2 ;;
  esac
done

# ── Detect the package manager ────────────────────────────────────────────
PM=""
for candidate in pacman apt-get dnf zypper; do
  if command -v "$candidate" >/dev/null 2>&1; then PM="$candidate"; break; fi
done
if [ -z "$PM" ]; then
  echo "No supported package manager found (pacman/apt-get/dnf/zypper)." >&2
  echo "Install the tools listed below manually." >&2
fi

SUDO=""
if [ "$(id -u)" -ne 0 ]; then SUDO="sudo"; fi

# ── Logical tool -> per-distro package name ───────────────────────────────
# Format of each entry: "purpose|binary|pacman|apt|dnf|zypper"
# An empty package field means "not packaged here / use another source".
TOOLS=(
  "FAT/exFAT mtools (mdir,mcopy,mformat)|mdir|mtools|mtools|mtools|mtools"
  "FAT mkfs/fsck|mkfs.fat|dosfstools|dosfstools|dosfstools|dosfstools"
  "exFAT mkfs/fsck|mkfs.exfat|exfatprogs|exfatprogs|exfatprogs|exfatprogs"
  "ext2/3/4 (mke2fs,debugfs,dumpe2fs)|debugfs|e2fsprogs|e2fsprogs|e2fsprogs|e2fsprogs"
  "ext file access (e2ls,e2cp)|e2ls|e2tools|e2tools|e2tools|e2tools"
  "NTFS (mkntfs,ntfsls,ntfsinfo)|ntfsls|ntfs-3g|ntfs-3g|ntfs-3g|ntfs-3g_ntfsprogs"
  "XFS (mkfs.xfs,xfs_db)|mkfs.xfs|xfsprogs|xfsprogs|xfsprogs|xfsprogs"
  "Btrfs (mkfs.btrfs,btrfs)|mkfs.btrfs|btrfs-progs|btrfs-progs|btrfs-progs|btrfsprogs"
  "F2FS (mkfs.f2fs,dump.f2fs)|mkfs.f2fs|f2fs-tools|f2fs-tools|f2fs-tools|f2fs-tools"
  "JFS (mkfs.jfs,jfs_debugfs)|jfs_debugfs|jfsutils|jfsutils|jfsutils|jfsutils"
  "ReiserFS (mkreiserfs,debugreiserfs)|debugreiserfs|reiserfsprogs|reiserfsprogs|reiserfs-utils|reiserfs"
  "OCFS2 (mkfs.ocfs2)|mkfs.ocfs2|ocfs2-tools|ocfs2-tools|ocfs2-tools|ocfs2-tools"
  "HFS/HFS+ classic (hmount,hls)|hls|hfsutils|hfsutils|hfsutils|hfsutils"
  "HFS+ mkfs/fsck|mkfs.hfsplus|hfsprogs|hfsprogs|hfsplus-tools|hfsutils"
  "UDF (mkudffs)|mkudffs|udftools|udftools|udftools|udftools"
  "ISO9660 (xorriso)|xorriso|libisoburn|xorriso|xorriso|xorriso"
  "ISO9660 (isoinfo,genisoimage)|isoinfo|cdrtools|genisoimage|genisoimage|cdrtools"
  "SquashFS (mksquashfs,unsquashfs)|unsquashfs|squashfs-tools|squashfs-tools|squashfs-tools|squashfs"
  "JFFS2/MTD (mkfs.jffs2,jffs2dump)|jffs2dump|mtd-utils|mtd-utils|mtd-utils|mtd-utils"
  "CramFS (mkcramfs)|mkfs.cramfs|cramfs-progs|cramfsprogs||"
  "7-Zip archive reader (7z)|7z|7zip|p7zip-full|p7zip|7zip"
  "zstd interop|zstd|zstd|zstd|zstd|zstd"
  "lz4 interop|lz4|lz4|lz4|lz4|lz4"
  "disk-image convert (qemu-img)|qemu-img|qemu-img|qemu-utils|qemu-img|qemu-tools"
)

pm_field() {
  # $1 = colon? no — fields are pipe-separated, index 3..6 by PM
  case "$PM" in
    pacman)  echo "$1" ;;
    apt-get) echo "$2" ;;
    dnf)     echo "$3" ;;
    zypper)  echo "$4" ;;
  esac
}

# Collect the package set to install (dedup, skip empties and already-present binaries).
declare -A WANT
SKIP_PRESENT=()
for entry in "${TOOLS[@]}"; do
  IFS='|' read -r purpose binary p_pac p_apt p_dnf p_zyp <<<"$entry"
  if command -v "$binary" >/dev/null 2>&1; then
    SKIP_PRESENT+=("$binary ($purpose)")
    continue
  fi
  pkg="$(pm_field "$p_pac" "$p_apt" "$p_dnf" "$p_zyp")"
  [ -n "$pkg" ] && WANT["$pkg"]="$purpose"
done

echo "── Package manager: ${PM:-none} ──"
if [ "${#SKIP_PRESENT[@]}" -gt 0 ]; then
  echo "Already present (skipped):"
  printf '  %s\n' "${SKIP_PRESENT[@]}"
fi
if [ "${#WANT[@]}" -eq 0 ]; then
  echo "Nothing to install — all known tools are already available."
  exit 0
fi
echo "To install:"
for pkg in "${!WANT[@]}"; do printf '  %-22s %s\n' "$pkg" "${WANT[$pkg]}"; done

if [ "$DRY_RUN" -eq 1 ] || [ -z "$PM" ]; then
  echo "(dry-run / no package manager: not installing)"
  exit 0
fi

# ── AUR helper (Arch): several packages (e2tools, hfsprogs, hfsutils,
# reiserfsprogs, ocfs2-tools) live only in the AUR, not the official repos.
AUR=""
if [ "$PM" = "pacman" ]; then
  for h in yay paru pikaur trizen; do
    if command -v "$h" >/dev/null 2>&1; then AUR="$h"; break; fi
  done
fi

# ── Install command per package manager ───────────────────────────────────
install_one() {
  local pkg="$1"
  case "$PM" in
    pacman)
      # Official repo first; fall back to the AUR helper for AUR-only packages.
      if $SUDO pacman -S --needed --noconfirm "$pkg" 2>/dev/null; then return 0; fi
      if [ -n "$AUR" ]; then
        echo "   .. not in official repos; trying AUR via $AUR"
        "$AUR" -S --needed --noconfirm "$pkg"; return $?
      fi
      return 1 ;;
    apt-get) $SUDO apt-get install -y "$pkg" ;;
    dnf)     $SUDO dnf install -y "$pkg" ;;
    zypper)  $SUDO zypper install -y "$pkg" ;;
  esac
}

if [ "$PM" = "apt-get" ]; then $SUDO apt-get update -y || true; fi
if [ "$PM" = "pacman" ] && [ -z "$AUR" ]; then
  echo "Note: no AUR helper (yay/paru) found — AUR-only packages (e2tools, hfsprogs, hfsutils, reiserfsprogs, ocfs2-tools) will be skipped."
fi

FAILED=()
for pkg in "${!WANT[@]}"; do
  echo "── installing $pkg (${WANT[$pkg]})"
  if ! install_one "$pkg"; then
    echo "   !! could not install $pkg (try an AUR helper / EPEL / another repo) — skipping"
    FAILED+=("$pkg")
  fi
done

echo
echo "── Summary ──"
if [ "${#FAILED[@]}" -gt 0 ]; then
  echo "Not installed automatically (install manually if you need those tests):"
  printf '  %s\n' "${FAILED[@]}"
  if [ "$PM" = "pacman" ]; then
    echo "On Arch these are AUR packages — install an AUR helper (e.g. 'pacman -S --needed yay' from an AUR build, or paru) and re-run, or build them from the AUR manually."
  fi
else
  echo "All requested packages installed."
fi
echo "Re-run the test suite; external-validation tests will now exercise the installed tools."
