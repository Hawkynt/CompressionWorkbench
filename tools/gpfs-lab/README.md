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
The IBM demo uses five 10 GiB NSDs, so one complete logical capture is roughly
50 GiB before filesystem-level sparse-file savings. Keep the corpus outside Git.

Each semantic change is followed by:

1. `tsdbfs inode` and `mmgetlocation -Y -L` for the selected live paths;
2. `sync` and a clean `mmumount`;
3. `mmfsckx --check-reserved-files-only`;
4. `mmfileid` for physical inode replica sectors reported by `tsdbfs`;
5. `mmlsdisk` and `mmlsnsd` identity/device mapping;
6. raw readout of **every** backing NSD;
7. SHA-256-addressed image filenames plus `manifest.tsv` and `SHA256SUMS`;
8. remount of `fs1` for the next controlled mutation.

A failed unmount, ambiguous NSD-to-device mapping, missing oracle command, missing
probe path, or fewer than two discovered NSDs aborts the capture before it is
eligible for the repository promotion gate.

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
structure. Use the oracle to constrain the search:

- correlate `tsdbfs` inode replica `disk:sector` values with the corresponding
  raw NSD and compare only paired captures around one metadata edit;
- infer candidate checksum fields/coverage from bytes that change with the IBM
  checksum and reject candidates on the next inode/corpus;
- use a file recreated/moved to another disk/sector to reject accidental
  disk-address encodings;
- use the isolated one-subblock create/delete pair to find a single allocation
  transition, then repeat across bit/word/record/region boundaries;
- cross-check allocation-map word orientation against `mmfsckx` actual/expected
  vectors, never against a self-generated expected value only.

`GpfsRawDiff` provides only representation-neutral byte/bit differencing.
`GpfsEvidenceManifest` verifies corpus completeness. Neither class contains a
claimed GPFS byte layout.

## 5. Promotion rules

A syntactically complete capture is evidence, not proof. `GpfsReadOnlyPromotionGate`
requires at least two **independent corpus IDs**, and for each corpus a raw parser
must independently agree with IBM tooling on all Stage-1 requirements:

- descriptor/disk mapping;
- inode location and checksum;
- direct and indirect addressing;
- inode allocation state;
- directory lookup;
- sparse/data-in-inode behavior;
- malformed/truncated metadata failing closed.

Only after that gate is genuinely satisfied should `GpfsFormatDescriptor` be
promoted to filesystem R/O.

R/W and maintenance remain a separate gate. Any mutation/rebuild must remount
under IBM Storage Scale and complete `mmfsckx` without corruption before the
corresponding capability can be advertised. A writer that merely matches its own
reader is not evidence.
