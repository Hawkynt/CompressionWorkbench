#!/usr/bin/env bash
set -euo pipefail

INSTALLER=${1:?usage: run-two-corpora.sh STORAGE_SCALE_DEVELOPER_INSTALLER [libvirt|virtualbox] [WORK_ROOT]}
PROVIDER=${2:-libvirt}
ROOT=${3:-$PWD/.gpfs-lab/gpfs-two-corpus}
REMOTE=${GPFS_CORPUS_ROOT:-/var/tmp/cw-gpfs-corpus}
SCRIPT_DIR=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)
REPO_ROOT=$(cd -- "$SCRIPT_DIR/../.." && pwd)
PROVISION="$SCRIPT_DIR/provision-vagrant.sh"
EXPORT="$SCRIPT_DIR/export-corpus.sh"
VERIFY="$SCRIPT_DIR/verify-corpora.sh"

[[ -f $INSTALLER ]] || { echo "installer not found: $INSTALLER" >&2; exit 2; }
[[ $PROVIDER == libvirt || $PROVIDER == virtualbox ]] || { echo "provider must be libvirt or virtualbox" >&2; exit 2; }
[[ $REMOTE == /* && $REMOTE =~ ^/[A-Za-z0-9._/-]+$ ]] || {
  echo "GPFS_CORPUS_ROOT must be an absolute path containing only letters, digits, '.', '_', '-', and '/'" >&2
  exit 2
}
for helper in "$PROVISION" "$EXPORT" "$VERIFY"; do
  [[ -x $helper ]] || { echo "required GPFS lab helper is not executable: $helper" >&2; exit 2; }
done
for cmd in vagrant dotnet find mkdir rm; do
  command -v "$cmd" >/dev/null || { echo "missing required command: $cmd" >&2; exit 2; }
done

if [[ -e $ROOT ]]; then
  [[ -d $ROOT ]] || { echo "work root exists and is not a directory: $ROOT" >&2; exit 3; }
  if find "$ROOT" -mindepth 1 -maxdepth 1 -print -quit | grep -q .; then
    echo "work root must be new or empty so provider backing disks cannot be reused: $ROOT" >&2
    exit 3
  fi
fi
mkdir -p "$ROOT"
ROOT=$(cd "$ROOT" && pwd)
LAB_A="$ROOT/lab-a"
LAB_B="$ROOT/lab-b"
EVIDENCE="$ROOT/evidence"
CORPUS_A="$EVIDENCE/corpus-a"
CORPUS_B="$EVIDENCE/corpus-b"
mkdir -p "$EVIDENCE"

run_corpus() {
  local lab=$1 corpus_id=$2
  (
    cd "$lab/$PROVIDER"
    vagrant ssh m1 -c \
      "sudo env GPFS_CORPUS_ID=$corpus_id GPFS_CORPUS_ROOT=$REMOTE /vagrant/gpfs-corpus/run-controlled-corpus.sh"
  )
}

destroy_lab() {
  local lab=$1
  (
    cd "$lab/$PROVIDER"
    vagrant destroy -f
  )
  rm -rf "$lab"
}

"$PROVISION" "$INSTALLER" "$PROVIDER" "$LAB_A"
run_corpus "$LAB_A" corpus-a
"$EXPORT" "$LAB_A" "$PROVIDER" "$REMOTE" "$CORPUS_A"

# Corpus B must not inherit provider-specific backing disks from A. Destroy the
# first Vagrant environment and remove its checkout before creating the second.
destroy_lab "$LAB_A"

"$PROVISION" "$INSTALLER" "$PROVIDER" "$LAB_B"
run_corpus "$LAB_B" corpus-b
"$EXPORT" "$LAB_B" "$PROVIDER" "$REMOTE" "$CORPUS_B"
"$VERIFY" "$CORPUS_A" "$CORPUS_B"

(
  cd "$REPO_ROOT"
  CWB_GPFS_CORPUS_A="$CORPUS_A" \
  CWB_GPFS_CORPUS_B="$CORPUS_B" \
    dotnet test Compression.Tests/Compression.Tests.csproj \
      -c Release \
      --filter 'FullyQualifiedName~Compression.Tests.Gpfs.GpfsCorpusExternalTests'
)

printf 'two independent GPFS corpora verified and ExternalFsInterop passed\n'
printf 'corpus A: %s\ncorpus B: %s\n' "$CORPUS_A" "$CORPUS_B"
printf 'lab B remains provisioned at %s for follow-up mutation/remount experiments\n' "$LAB_B"
