#!/usr/bin/env bash
set -euo pipefail

LAB=${1:?usage: export-corpus.sh LAB_DIR [libvirt|virtualbox] REMOTE_CORPUS_ROOT HOST_DESTINATION}
PROVIDER=${2:-libvirt}
REMOTE=${3:?usage: export-corpus.sh LAB_DIR [libvirt|virtualbox] REMOTE_CORPUS_ROOT HOST_DESTINATION}
DEST=${4:?usage: export-corpus.sh LAB_DIR [libvirt|virtualbox] REMOTE_CORPUS_ROOT HOST_DESTINATION}
SCRIPT_DIR=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)
VERIFY="$SCRIPT_DIR/verify-corpora.sh"

[[ $PROVIDER == libvirt || $PROVIDER == virtualbox ]] || {
  echo "provider must be libvirt or virtualbox" >&2
  exit 2
}
[[ -d "$LAB/$PROVIDER" ]] || { echo "provider directory not found: $LAB/$PROVIDER" >&2; exit 2; }
[[ -x $VERIFY ]] || { echo "corpus verifier not found: $VERIFY" >&2; exit 2; }
for cmd in vagrant rsync ssh mktemp find grep mkdir; do
  command -v "$cmd" >/dev/null || { echo "missing required command: $cmd" >&2; exit 2; }
done

mkdir -p "$DEST"
DEST=$(cd "$DEST" && pwd)
if find "$DEST" -mindepth 1 -maxdepth 1 -print -quit | grep -q .; then
  echo "host destination must be empty: $DEST" >&2
  exit 3
fi

ssh_config=$(mktemp)
cleanup() { rm -f "$ssh_config"; }
trap cleanup EXIT

(
  cd "$LAB/$PROVIDER"
  vagrant ssh-config m1
) >"$ssh_config"

# IBM/StorageScaleVagrant maps ../setup into /vagrant with Vagrant's rsync
# synced-folder type. That direction is host -> guest only, so evidence under
# /var/tmp must be explicitly pulled before a destructive reprovision. Use the
# exact Vagrant SSH identity/port and remote sudo rsync so root-owned raw images
# remain readable. --sparse preserves zero runs in the copied NSD images.
rsync \
  --archive \
  --hard-links \
  --sparse \
  --human-readable \
  --info=progress2 \
  --rsync-path='sudo rsync' \
  -e "ssh -F $ssh_config" \
  "m1:${REMOTE%/}/" \
  "$DEST/"

"$VERIFY" "$DEST"
printf 'exported and verified GPFS corpus %s -> %s\n' "$REMOTE" "$DEST"
