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
`$StorageScale_version` setting to match the supplied installer, builds and
registers IBM's provider-specific `StorageScale_base` box if it is not already
installed, provisions the VM, and requires the documented final five-NSD `fs1`
topology (`nsd1`, `nsd2`, `nsd3`, `nsd6`, `nsd7`). Override
`STORAGE_SCALE_VAGRANT_REF` intentionally when testing a newer IBM lab revision.

The base-box step follows IBM's own `prep-box` Vagrantfile and therefore needs
normal Internet access for its upstream Linux box/RPM downloads. The separately
licensed Storage Scale installer is never fetched by this script.

## 2. Run the controlled corpus inside m1

The provisioning script places the capture tools in the Vagrant provision tree.
Inside the management VM:

```bash
sudo GPFS_CORPUS_ROOT=/var/tmp/cw-gpfs-corpus \
  /vagrant/gpfs-corpus/run-controlled-corpus.sh
```

`GPFS_CORPUS_ROOT` must have enough capacity for every NSD image at every step.
Each capture's logical size is the sum of the backing devices reported by the
provisioned lab. The harness writes zero ranges as sparse host-file holes without
changing logical bytes or SHA-256 digests, but capacity planning should still
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
120-inode-allocated
121-inode-freed
130-block-baseline
131-block-allocated
132-block-freed
```

The `055-rebalanced` name is retained as a stable corpus ID, but the transition
is deterministic rather than an ordinary balance pass. IBM's demo placement
policy sends ordinary files such as `block-plus-one.bin` to the `capacity` pool.
The runner verifies that placement at `050`, assigns the file to `system` with
`mmchattr -P system -I defer`, runs `mmrestripefile -p`, verifies the resulting
`system` placement, and captures `055`. This forces a storage-pool/NSD-set change
before any disk-address representation hypothesis is considered.

`105` uses IBM's immediate two-replica file attributes and restriping, then lets
`tsdbfs`/`mmgetlocation` prove whether the requested replicas actually exist.
The final transitions deliberately decouple the two allocation maps. `120`/`121`
create and delete an **empty** file, changing inode allocation without user-data
allocation. `130` establishes an already-allocated empty inode as a baseline;
`131` writes exactly one subblock into it and `132` truncates it back to zero,
changing block allocation without allocating or freeing that inode.

Each capture performs this order:

1. archive `mmlsdisk`, `mmlsnsd`, filesystem UID/version and topology;
2. run report-only `mmfsckx --check-reserved-files-only` while the filesystem is
   mounted, as required by IBM's online checker;
3. archive `tsdbfs inode` for reserved inodes 0, 1, 2, 4, 5 and 38 plus every
   selected user inode;
4. archive `mmgetlocation -Y -L` for selected live paths and `mmfileid` for every
   physical `disk:sector` printed anywhere in those `tsdbfs` inode reports;
5. reject the capture if any requested `mmfileid` lookup fails;
6. `sync` and cleanly unmount the filesystem;
7. refuse capture if a GPFS mount still exists;
8. read **every** backing NSD into a full logical raw image;
9. verify captured byte length, name images by SHA-256, and write `manifest.tsv`
   plus `SHA256SUMS`;
10. remount `fs1` for the next controlled mutation.

A failed checker, `mmfileid` lookup, unmount, ambiguous NSD-to-device mapping,
missing oracle command, missing probe path, short image, or fewer than two
discovered NSDs aborts the capture before it is eligible for the repository
promotion gate.

## 3. Corpus layout and integrity

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

Before deriving anything from a corpus, verify every digest, the fixed transition
sequence, topology invariants and filesystem identity. For two promotion corpora:

```bash
./tools/gpfs-lab/verify-corpora.sh /path/to/corpus-a /path/to/corpus-b
```

The verifier rejects duplicate corpus IDs and duplicate filesystem UIDs, so two
runs against the same formatted filesystem do not masquerade as independent
evidence.

## 4. Derivation workflow

Do not search entire NSDs for attractive byte patterns and call the first match a
structure. Use the IBM oracles to constrain the search.

For placement, keep the oracle roles distinct. `mmgetlocation -Y -L` reports the
logical chunk offset plus NSD name and GPFS disk ID; it does **not** report a
physical sector. `tsdbfs inode` reports the physical inode replicas and, in its
`Disk pointers [...]` section, the file's physical data-pointer `disk:sector`
values. `mmfileid` then supplies the independent reverse check from those sectors
to their owning reserved object or user inode/path. A disk-address encoding does
not survive unless all three views remain consistent.

Use that triangle as follows:

- correlate each `tsdbfs` inode replica `disk:sector` with the corresponding raw
  NSD using the logical sector size recorded by `mmlsdisk -L`;
- use `GpfsRawCorrelation.ReadInodeReplicas` to read only those IBM-identified
  inode bytes instead of scanning whole NSDs;
- intersect checksum-value candidates at a stable byte offset/endian across
  replicas and captures before testing any checksum algorithm or coverage;
- parse `tsdbfs`'s `Disk pointers [...]` with `GpfsTsdbfsPointerParser`; compare
  `050-block-plus-one` with the forced capacity-to-system `055-rebalanced`
  transition, and retain packed-address candidates only when the independently
  reported `disk:sector` changes and the same raw-field hypothesis predicts both
  values;
- use `GpfsMmfileidAggregateParser` to keep each `mmfileid` result bound to the
  query disk ID; require pointer sectors to map back to the expected inode/path;
- infer inode-map changes only from reserved inode 2 across
  `110-delete -> 120-inode-allocated -> 121-inode-freed`; the probe file is empty,
  so user-data allocation is not coupled to the inode transition;
- infer block-map changes only from reserved inode 1 across
  `130-block-baseline -> 131-block-allocated -> 132-block-freed`; the file's inode
  exists throughout, so the one-subblock allocation is not coupled to inode
  allocation;
- require each allocation/free pair to reverse the same candidate bit before it
  contributes to the model;
- feed multiple known allocation transitions to
  `GpfsRawCorrelation.InferBitmapOrder`; one transition deliberately proves
  neither LSB-first nor MSB-first numbering;
- repeat bitmap transitions across byte, word, record, region and segment
  boundaries before generalising the offset function;
- constrain raw 64-bit allocation-map word order against independent `mmfsckx`
  actual/expected vectors with `FindWordVectorCandidates`, never against a
  self-generated expected value only.

`GpfsRawDiff` provides representation-neutral byte/bit differencing.
`GpfsEvidenceManifest` verifies an individual capture and `GpfsEvidenceCorpus`
requires the entire controlled series. `GpfsRawCorrelation` produces candidate
field layouts, not accepted format rules. None of those classes contains a
claimed GPFS byte layout.

When two real corpora are available, set `CWB_GPFS_CORPUS_A` and
`CWB_GPFS_CORPUS_B` before running the `ExternalFsInterop` tests. The optional
`GpfsCorpusExternalTests` then opens only IBM-identified raw regions, requires
`mmfileid` coverage for every `tsdbfs` physical address, and exercises the paired
`050`/`055` address-candidate intersection. Without those environment variables,
normal CI skips the proprietary-corpus tests.

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
