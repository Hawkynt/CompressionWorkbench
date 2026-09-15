#!/usr/bin/env bash
set -euo pipefail

(( $# >= 1 )) || { echo "usage: verify-corpora.sh CORPUS_ROOT [CORPUS_ROOT ...]" >&2; exit 2; }
for cmd in sha256sum awk sort grep basename; do command -v "$cmd" >/dev/null || { echo "missing: $cmd" >&2; exit 2; }; done

required=(
  000-anchor
  010-empty
  020-tiny
  030-one-subblock
  040-one-block
  050-block-plus-one
  055-rebalanced
  060-indirect
  070-sparse
  080-directory
  090-links
  100-xattr-acl
  105-replicated
  110-delete
  120-inode-allocated
  121-inode-freed
  130-block-baseline
  131-block-allocated
  132-block-freed
)
required_artifacts=(mmfsckx tsdbfs mmfileid mmgetlocation mmlsdisk mmlsnsd)

declare -A seen_corpus seen_uid
for root in "$@"; do
  root=$(readlink -f "$root")
  [[ -d $root ]] || { echo "corpus root not found: $root" >&2; exit 3; }

  corpus_id=
  filesystem_uid=
  topology=
  format_version=
  storage_version=

  for capture_id in "${required[@]}"; do
    dir="$root/$capture_id"
    manifest="$dir/manifest.tsv"
    [[ -f $manifest && -f $dir/SHA256SUMS ]] || { echo "incomplete capture: $dir" >&2; exit 3; }

    (cd "$dir" && sha256sum -c --strict SHA256SUMS)

    actual_capture=$(awk -F '\t' '$1=="meta" && $2=="capture-id" {print $3}' "$manifest")
    actual_corpus=$(awk -F '\t' '$1=="meta" && $2=="corpus-id" {print $3}' "$manifest")
    actual_uid=$(awk -F '\t' '$1=="meta" && $2=="filesystem-uid" {print $3}' "$manifest")
    actual_format=$(awk -F '\t' '$1=="meta" && $2=="format-version" {print $3}' "$manifest")
    actual_storage=$(awk -F '\t' '$1=="meta" && $2=="storage-scale-version" {print $3}' "$manifest")
    state=$(awk -F '\t' '$1=="meta" && $2=="capture-state" {print $3}' "$manifest")

    [[ $actual_capture == "$capture_id" ]] || { echo "$dir has capture-id '$actual_capture'" >&2; exit 4; }
    [[ $state == unmounted-clean ]] || { echo "$dir was not captured unmounted-clean" >&2; exit 4; }

    capture_topology=$(awk -F '\t' '$1=="nsd" {print $2 ":" $3 ":" $4 ":" $5}' "$manifest" | sort | tr '\n' ';')
    nsd_count=$(awk -F '\t' '$1=="nsd" {n++} END {print n+0}' "$manifest")
    (( nsd_count >= 2 )) || { echo "$dir is not multi-NSD" >&2; exit 4; }

    while IFS=$'\t' read -r _ nsd _ _ _ digest image; do
      [[ -f $dir/$image ]] || { echo "$dir missing raw image $image" >&2; exit 4; }
      [[ $(basename "$image") == *".$digest.img" ]] || { echo "$dir raw image filename is not digest-addressed for $nsd" >&2; exit 4; }
    done < <(awk -F '\t' '$1=="nsd"' OFS='\t' "$manifest")

    for kind in "${required_artifacts[@]}"; do
      grep -q $'^artifact\t'"$kind"$'\t' "$manifest" || { echo "$dir missing oracle family $kind" >&2; exit 4; }
    done

    if [[ -z $corpus_id ]]; then
      corpus_id=$actual_corpus
      filesystem_uid=$actual_uid
      format_version=$actual_format
      storage_version=$actual_storage
      topology=$capture_topology
    else
      [[ $actual_corpus == "$corpus_id" ]] || { echo "$root crosses corpus IDs" >&2; exit 5; }
      [[ $actual_uid == "$filesystem_uid" ]] || { echo "$root crosses filesystem UIDs" >&2; exit 5; }
      [[ $actual_format == "$format_version" ]] || { echo "$root crosses format versions" >&2; exit 5; }
      [[ $actual_storage == "$storage_version" ]] || { echo "$root crosses Storage Scale versions" >&2; exit 5; }
      [[ $capture_topology == "$topology" ]] || { echo "$root changes NSD topology" >&2; exit 5; }
    fi
  done

  [[ -n $corpus_id && -n $filesystem_uid ]] || { echo "$root lacks corpus identity" >&2; exit 5; }
  [[ -z ${seen_corpus[$corpus_id]:-} ]] || { echo "duplicate corpus ID: $corpus_id" >&2; exit 6; }
  [[ -z ${seen_uid[$filesystem_uid]:-} ]] || { echo "corpora are not independently formatted; duplicate UID: $filesystem_uid" >&2; exit 6; }
  seen_corpus[$corpus_id]=1
  seen_uid[$filesystem_uid]=1
  printf 'verified corpus=%s filesystem-uid=%s format=%s storage-scale=%s\n' \
    "$corpus_id" "$filesystem_uid" "$format_version" "$storage_version"
done

if (( $# >= 2 )); then
  echo "verified independent corpus IDs and filesystem UIDs"
fi
