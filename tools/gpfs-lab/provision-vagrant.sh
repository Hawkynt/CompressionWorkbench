#!/usr/bin/env bash
set -euo pipefail

INSTALLER=${1:?usage: provision-vagrant.sh STORAGE_SCALE_DEVELOPER_INSTALLER [libvirt|virtualbox] [LAB_DIR]}
PROVIDER=${2:-libvirt}
LAB=${3:-$PWD/.gpfs-lab/StorageScaleVagrant}
REF=${STORAGE_SCALE_VAGRANT_REF:-64706486022d29cd9d5d89f2fa18ec509d2099e8}

[[ -f $INSTALLER ]] || { echo "installer not found: $INSTALLER" >&2; exit 2; }
[[ $PROVIDER == libvirt || $PROVIDER == virtualbox ]] || { echo "provider must be libvirt or virtualbox" >&2; exit 2; }
for cmd in git vagrant sed grep cp; do command -v "$cmd" >/dev/null || { echo "missing: $cmd" >&2; exit 2; }; done

if [[ ! -d $LAB/.git ]]; then
  mkdir -p "$(dirname "$LAB")"
  git clone https://github.com/IBM/StorageScaleVagrant.git "$LAB"
fi
git -C "$LAB" fetch origin "$REF"
git -C "$LAB" checkout --detach "$REF"

base=$(basename "$INSTALLER")
if [[ $base =~ Storage_Scale_Developer-([0-9]+\.[0-9]+\.[0-9]+\.[0-9]+)-x86_64-Linux-install ]]; then
  version=${BASH_REMATCH[1]}
else
  echo "installer filename does not match IBM's documented Developer Edition naming" >&2
  exit 3
fi

cp -f "$INSTALLER" "$LAB/software/$base"
sed -E -i "s/\$StorageScale_version = \"[^\"]+\"/\$StorageScale_version = \"$version\"/" "$LAB/shared/Vagrantfile.common"

mkdir -p "$LAB/setup/gpfs-corpus"
cp -f "$(dirname "$0")/capture.sh" "$LAB/setup/gpfs-corpus/capture.sh"
cp -f "$(dirname "$0")/run-controlled-corpus.sh" "$LAB/setup/gpfs-corpus/run-controlled-corpus.sh"
chmod +x "$LAB/setup/gpfs-corpus/"*.sh

(
  cd "$LAB/$PROVIDER"
  vagrant up
  disk_table=$(vagrant ssh -c "sudo mmlsdisk fs1 -L" | tr -d '\r')
  for nsd in nsd1 nsd2 nsd3 nsd6 nsd7; do
    grep -Eq "^[[:space:]]*$nsd[[:space:]]" <<<"$disk_table" || {
      echo "provisioned fs1 does not contain expected IBM demo NSD $nsd" >&2
      exit 4
    }
  done
)

echo "Storage Scale $version lab provisioned from IBM/StorageScaleVagrant@$REF"
echo "Run the corpus inside m1: sudo /vagrant/gpfs-corpus/run-controlled-corpus.sh"
