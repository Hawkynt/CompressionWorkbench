# GPFS raw-evidence lab

This directory turns `docs/GPFS-ON-DISK.md` into an executable clean-room
experiment. It does **not** redistribute IBM Storage Scale and does not contain
any proprietary on-disk implementation material.

IBM's `StorageScaleVagrant` repository is Apache-2.0. Storage Scale Developer
Edition is separate IBM software and must be downloaded/accepted by the operator.
The provisioning script therefore takes the installer as an explicit input and
never downloads, commits, or caches it in this repository.

## 1. Provision the IBM five-NSD demo

On a Linux host with Vagrant plus libvirt or VirtualBox:

```bash
./tools/gpfs-lab/provision-vagrant.sh \
  /path/to/Storage_Scale_Developer-6.0.1.0-x86_64-Linux-install \
  libvirt
```

The script pins IBM/StorageScaleVagrant to commit
`64706486022d29cd9d5d89f2fa18ec509d2099e8`, updates only the documented
`$StorageScale_version` setting to match the supplied installer, provisions the
VM, and requires the documented five-NSD `fs1` topology (`nsd1`, `nsd2`, `nsd3`,
`nsd6`, `nsd7`). Override `STORAGE_SCALE_VAGRANT_REF` intentionally when testing
a newer IBM lab revision.

## 2. Run the controlled corpus inside m1

The provisioning script places the capture tools in the Vagrant provision tree.
Inside the management VM:

```bash
sudo GPFS_CORPUS_ROOT=/var/tmp/cw-gpfs-corpus \
  /vagrant/gpfs-corpus/run-controlled-corpus.sh
```

`GPFS_CORPUS_ROOT` must have enough capacity for every NSD image at every step.
The IBM demo uses five 10 GiB NSDs, so each capture is 50 GiB of logical raw
address space. The harness writes zero ranges as sparse host-file holes without
changing the logical bytes or SHA-256 digest, but capacity planning should still
assume the non-zero metadata/data footprint of all captures. Keep the corpus
outside Git.

The controlled sequence is deliberately fixed and promotion-gated:

```text
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
```

`050`/`055` bracket an IBM `mmrestripefile -b` operation so the verifier can
require a real placement change before accepting a disk-address encoding.
`105` uses IBM's immediate two-replica file attributes and restriping, then lets
`tsdbfs`/`mmgetlocation` prove whether the requested replicas actually exist.
Neither operation is accepted merely because its command returned success.

Each capture performs this order:

1. archive `mmlsdisk`, `mmlsnsd`, filesystem UID/version and topology;
2. run report-only `mmfsckx --check-reserved-files-only` while the filesystem is
   mounted, as required by IBM's online checker;
3. archive `tsdbfs inode` for reserved inodes 0, 1, 2, 4, 5 and 38 plus every
   selected user inode;
4. archive `mmgetlocation -Y -L` for selected live paths and `mmfileid` for the
   physical `disk:sector` addresses printed by `tsdbfs`;
5. `sync` and cleanly unmount the filesystem;
6. refuse capture if a GPFS mount still exists;
7. read **every** backing NSD into a full logical raw image;
8. verify captured byte length, name images by SHA-256, and write `manifest.tsv`
   plus `SHA256SUMS`;
9. remount `fs1` for the next controlled mutation.

A failed checker, unmount, ambiguous NSD-to-device mapping, missing oracle
command, missing probe path, short image, or fewer than two discovered NSDs aborts
the capture before it is eligible for the repository promotion gate.

## 3. Corpus layout

A capture directory contains:

```text
manifest.tsv
SHA256SUMS
oracle/
  mmfsckx.txt
  mmfileid.txt
  mmlsdisk.txt
  mmlsnsd.txt
  tsdbfs-reserved-0.txt
  tsdbfs-reserved-1.txt
  tsdbfs-reserved-2.txt
  tsdbfs-*.txt
  mmgetlocation-*.txt
  ...
raw/
  nsd1.<sha256>.img
  nsd2.<sha256>.img
  ...
```

`manifest.tsv` intentionally has a trivial line format so it is inspectable and
can be parsed without another dependency:

```text
meta<TAB>key<TAB>value
nsd<TAB>name<TAB>disk-id<TAB>device-size<TAB>sector-bytes<TAB>sha256<TAB>image-path
artifact<TAB>kind<TAB>sha256<TAB>path
```

The six required oracle kinds are `mmfsckx`, `tsdbfs`, `mmfileid`,
`mmgetlocation`, `mmlsdisk`, and `mmlsnsd`.

## 4. Derivation workflow

Do not search entire NSDs for attractive byte patterns and call the first match a
structure. Use the IBM oracle to constrain the search:

- correlate each `tsdbfs` inode replica `disk:sector` with the corresponding raw
  NSD, then identify inode field offsets only from paired single-change captures;
- infer candidate inode checksum field, algorithm and coverage from bytes that
  move with IBM's reported checksum, then reject every candidate that fails on a
  second inode and a second independently formatted filesystem;
- compare `050-block-plus-one` with `055-rebalanced`; accept disk-address packing
  only when IBM reports a real placement change and the candidate encoding
  predicts both placements;
- compare `030-one-subblock` and `110-delete` around the isolated allocation and
  use the reserved inode-1/inode-2 data to derive block-map and inode-map byte/bit
  transitions;
- repeat bitmap transitions across byte, word, record, region and segment
  boundaries before generalising the offset function;
- cross-check block-map word orientation against independent `mmfsckx`
  actual/expected vectors, never against a self-generated expected value only.

`GpfsRawDiff` provides only representation-neutral byte/bit differencing.
`GpfsEvidenceManifest` verifies an individual capture and `GpfsEvidenceCorpus`
requires the entire controlled series. None of those classes contains a claimed
GPFS byte layout.

## 5. Promotion rules

A syntactically complete capture is evidence, not proof. `GpfsReadOnlyPromotionGate`
requires at least two complete **independent corpora** with different corpus IDs
and different filesystem UIDs. Re-run after reformat/reprovision; merely running
the script twice against the same formatted `fs1` cannot satisfy independence.

For each corpus the raw parser must independently agree with IBM tooling on all
Stage-1 requirements:

- descriptor -> NSD/disk mapping;
- inode record offsets/layout;
- inode checksum algorithm and coverage;
- packed physical disk-address representation;
- direct and at least one indirect addressing level;
- inode allocation-map bit/record mapping;
- block allocation-map bit/record mapping;
- directory lookup;
- sparse/data-in-inode behavior;
- malformed/truncated metadata failing closed.

Only after that gate is genuinely satisfied should `GpfsFormatDescriptor` be
promoted to filesystem R/O.

R/W and maintenance remain a separate gate. `GpfsMutationPromotionGate` requires
a mutated/rebuilt disposable filesystem to remount under IBM Storage Scale, pass
`mmfsckx`, preserve namespace and file contents, and preserve links, ACLs/xattrs,
sparse ranges and replicas before any mutation/compact/defrag/wipe/shrink/layout
or purge capability may be advertised. A writer that merely matches its own
reader is not evidence.
