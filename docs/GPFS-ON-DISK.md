# GPFS / IBM Storage Scale — on-disk derivation notes

GPFS (now IBM Storage Scale) has no public byte-level on-disk specification that
is sufficient to implement an independent inode/allocation-map reader or writer.
This document is therefore the clean-room firewall for that work: it records what
IBM's own tools say about real file systems, what follows mechanically from those
observations, and what is still unknown.

No IBM implementation source, decompiler output, or proprietary binary structure
has been copied into this repository. IBM's diagnostic tools are behavioral
oracles only. Numeric identifiers, disk addresses and map words below are factual
interoperability data printed by those tools.

## Evidence classes

Every claim in this document belongs to one of three classes:

- **Verified** — stated by current IBM documentation or printed by an IBM tool in
  a published real-file-system example.
- **Derived** — follows mechanically from two or more verified observations, but
  still needs a raw-image experiment before it may become a binary parser rule.
- **Unknown** — do not encode it into a reader or writer yet.

That distinction matters. `7:332464` being a disk address is verified. Which
bytes inside an inode encode disk 7 and sector 332464 is not.

## Verified filesystem model

IBM describes a GPFS file system as a set of disks containing file data,
metadata and system files. A file-system descriptor at a fixed position on each
disk identifies the disk and its place in the filesystem. User files use inodes,
indirect blocks and data blocks; sufficiently small files may store their data in
the inode itself.

The allocation unit is a **subblock**. The block allocation map has one unit per
subblock and is split into separately locked regions on disk-sector boundaries.
The map layout is selected at format time (`cluster` or `scatter`) and cannot be
changed later.

Sources:

- <https://www.ibm.com/docs/en/storage-scale/6.0.0?topic=ga-use-disk-storage-file-structure-within-gpfs-file-system>
- <https://www.ibm.com/docs/en/storage-scale/6.0.0?topic=considerations-block-allocation-map>

## Physical disk addresses

IBM diagnostics consistently print a physical address as:

    disk-id:sector-number

Examples from IBM output include:

    inode @9:7962368
    disk address 7:332464
    Inode address: 7:520143360 10:780184064

`mmgetlocation -Y -L` independently exposes the same disk IDs alongside the NSD
name and file logical offset. `mmlsdisk -L` maps those numeric disk IDs to the
NSDs in the filesystem. This makes the following semantic type **verified**:

    DiskAddress = (diskId, sectorNumber)

It does **not** yet establish the byte encoding of a `DiskAddress` inside an
inode or indirect block.

Sources:

- <https://www.ibm.com/docs/en/storage-scale/5.2.3?topic=locality-mmgetlocation>
- <https://www.ibm.com/docs/en/storage-scale/6.0.0?topic=reference-mmfsckx-command>
- <https://www.ibm.com/support/pages/node/7246086>

## Reserved metadata files

Current `mmfsckx` output names the reserved files and their inode numbers. The
following are verified across IBM's published examples:

| Reserved object | inode |
| --- | ---: |
| inode file | 0 |
| block allocation map | 1 |
| inode allocation map | 2 |
| access-control-list data | 4 |
| extended-attribute data | 5 |
| fileset metadata | 38 |

`mmfileid` additionally demonstrates inode 7 as a log file in its published
example. That is evidence for the example filesystem, not yet a promise that
inode 7 has the same role in every format generation.

`mmfsckx` also prints format-dependent geometry for these files. A current
format-39 example with a 14-disk system pool reports:

    format version                 39.00
    allocation map layout          vertical
    allocation type                scatter
    subblocks per full block       512
    sector                         512 B
    metadata subblock              8 KiB
    metadata full block            4 MiB
    data subblock                  8 KiB
    data full block                4 MiB
    inode                          4 KiB
    indirect block                 32 KiB
    EA overflow block              64 KiB
    directory block                256 KiB

For that filesystem the same command reports:

    inode file:       recordsPerBlock=1024 inodeSpaceMask=0x0 inodeBlockMask=0xFF
    block alloc map:  disks=14 regions=784 segments=2
    inode alloc map:  regions=521 segments=1 iallocSpaceMask=0x0 iallocSegmtMask=0x0

Older/smaller examples have different block sizes, record counts and masks. Those
values therefore belong to filesystem geometry, not hard-coded GPFS constants.

Source:

- <https://www.ibm.com/docs/en/storage-scale/6.0.0?topic=reference-mmfsckx-command>

## Inode semantic model

`tsdbfs <fs> inode <n>` is an IBM diagnostic oracle for an inode. A current IBM
support example prints, among other fields:

    Inode 64 [64] snap 0 (index 64 in block 0)
    Inode address: 7:520143360 10:780184064 size 4096 nAddrs 330
    indirectionLevel=INDIRECT status=RESERVED
    objectVersion=0 generation=0x1 nlink=1
    blocksize code=7 (128 subblocks)
    lastBlockSubblocks=128
    checksum=0x609701E6 is Valid
    fileSize=4353687552 nFullBlocks=1038
    currentMetadataReplicas=2 maxMetadataReplicas=2
    currentDataReplicas=2 maxDataReplicas=2
    dataPoolIndex=7

The semantic model checked into `GpfsOracleModel.cs` therefore records:

- inode number, snapshot id and index within an inode block;
- every physical replica address of the inode record;
- inode size and address-slot count;
- indirection level and lifecycle status;
- object version, generation and link count;
- block-size code and last-block subblock count;
- checksum value plus whether the IBM tool considers it valid;
- logical file size and full-block count;
- current/maximum metadata and data replica counts;
- data-pool index.

These fields are **not** assigned binary offsets yet. The tool output proves the
semantics, not their representation.

Source:

- <https://www.ibm.com/support/pages/node/7246086>

## Allocation-map semantic model

`mmfsckx` validates the map by reconstructing expected ownership from metadata
and comparing it with the allocation-map system file. Its published corruption
examples expose both sides as words. One real example reports:

    !Block 3:188416 has map status:
        0x0000000000000000 ...
      expected:
        0x0001FFFFF8000000 ...

Another reports a duplicate address `7:332464` referenced by two different
inodes, then shows the expected map words for the corresponding block. This is
strong enough to model an allocation-map observation as:

    (disk address of map-covered block, actual word vector, expected word vector)

It is **not** strong enough to claim which raw allocation-map byte covers a given
sector. The following are still unknown and require paired raw captures:

1. word endianness in the reserved map file;
2. bit direction within each word;
3. the mapping from `(segment, region, disk, block/subblock)` to byte offset in
   inode 1;
4. padding/checksum/header bytes around each map record;
5. format-version differences between 33, 38, 39 and older layouts.

The inode allocation map has the same clean-room rule. We know inode 2 owns it,
`mmfsckx` reports ranges and allocation-state disagreements, and masks/region
counts are observable. Raw bit placement remains unverified.

Sources:

- <https://www.ibm.com/docs/en/storage-scale/6.0.0?topic=reference-mmfsckx-command>
- <https://www.ibm.com/docs/en/storage-scale/5.2.3?topic=reference-mmfsck-command>

## `mmfileid` closes the ownership loop

`mmfileid` takes suspect physical sector ranges on an NSD and reports what owns
them. IBM's documented example identifies sectors as:

    Block allocation map (inode 1)
    ACL Data file (inode 4)
    Log File (inode 7)
    inode 14336 /gpfsB/tesDir/testFile.out

That lets a raw-image experiment ask both directions:

- `mmgetlocation`: file logical offset -> NSD/disk id;
- `mmfileid`: NSD sector -> reserved object or user inode/path.

A raw parser does not get promoted until those two directions agree with its own
answer on the same image.

Source:

- <https://www.ibm.com/docs/en/storage-scale/5.2.3?topic=commands-mmfileid-command>

## Real multi-NSD lab target

IBM publishes an Apache-2.0 Vagrant environment for the proprietary Storage Scale
Developer Edition:

- <https://github.com/IBM/StorageScaleVagrant>

The repository does not redistribute Storage Scale itself; IBM requires the
Developer Edition installer to be downloaded separately. Its current demo is a
particularly useful oracle target because its `fs1` filesystem is documented as
having **five NSDs** and 4 MiB blocks. IBM's README shows the filesystem and NSD
configuration, so the lab topology is independently reproducible without
inventing our own GPFS setup.

The IBM Vagrant helper code is Apache-2.0, compatible with this repository. We do
not need to copy it: use it to provision the oracle. Storage Scale itself remains
IBM software and is never vendored here.

## Capture protocol

Use only a disposable Developer Edition filesystem. Do not run these experiments
on production data. `tsdbfs patch` is explicitly outside the protocol.

For every capture, record the Storage Scale version and collect these read-only
or report-only views before taking raw NSD images:

    mmlsfs <fs> -Y
    mmlsdisk <fs> -L -Y
    mmlsnsd -L -Y
    mmfsckx <fs> --check-reserved-files-only
    mmfsadm dump stripe
    mmfsadm dump ialloc
    tsdbfs <fs> desc

For every selected inode also collect:

    tsdbfs <fs> inode <inode-number>
    mmgetlocation -f <path> -Y -L

For selected sector ranges use `mmfileid` exactly as IBM documents it. For each
backing NSD, `mmfsadm test readdescraw <device>` is useful for descriptor
cross-checks; it is a diagnostic command rather than a public format contract,
so any result derived from it must be corroborated by raw bytes.

### Controlled corpus

Build one object at a time and capture after each step:

1. empty regular file;
2. tiny file expected to remain data-in-inode;
3. exactly one subblock of non-zero deterministic data;
4. exactly one full block;
5. one full block plus one byte;
6. file large enough to force one indirect level;
7. sparse file with holes at known logical offsets;
8. small directory, then enough entries to grow its directory representation;
9. symlink and hard link;
10. file with ACL and xattr data;
11. replicated file spanning at least two NSDs;
12. delete one known file and capture again.

Do not use all-zero payloads as the only data corpus: sparse/zero optimizations can
make a physically empty block look like an addressing rule. Use deterministic
non-zero bytes and record SHA-256 for every file.

### Raw NSD images

The useful experiment is **paired** images: before and after exactly one semantic
change. The filesystem should be cleanly unmounted before each raw capture so an
image is internally consistent. Dump every NSD in the filesystem, not merely the
NSD where `mmgetlocation` says the data landed; metadata replicas and allocation
maps can change elsewhere.

Keep a manifest containing, for every image:

    Storage Scale version / format version
    filesystem UID
    NSD name and numeric disk ID
    backing-device size and logical sector size
    SHA-256 of the complete raw image
    mmlsdisk/mmlsnsd output tying the image to the disk ID
    operation performed since the preceding capture

The raw images themselves do not need to be committed. Small, mechanically
extracted byte ranges may be checked in once their provenance and offset are
recorded.

## Experiments that establish the binary model

The next implementation stage should answer these in order.

### 1. Inode-number -> inode-record address

For several inodes in different inode blocks/filesets:

1. obtain physical inode replicas with `tsdbfs inode`;
2. read the 4 KiB bytes at each reported `disk:sector` from the raw NSD image;
3. identify fields that change under one controlled metadata edit;
4. repeat on another inode block and another format version.

Only after the same formula predicts addresses that `tsdbfs` independently
reports should it become reader code.

### 2. Disk-address byte representation

Choose a one-block file whose location is known from `mmgetlocation`. Search only
the independently identified inode/indirect-block bytes for that exact
`disk:sector` pair under candidate endian/packing rules. Move/recreate the file so
both disk and sector change, then reject every encoding that does not predict the
second capture.

### 3. Inode allocation-map bit order

Create one file, record its inode number, capture; delete it, capture again.
`mmfsckx` supplies the expected allocation state. Diff inode 2's raw data and
require exactly the predicted bit transition. Repeat across word, record, region
and segment boundaries.

### 4. Block allocation-map bit order

Allocate/deallocate known subblocks and use `mmgetlocation` plus `mmfileid` to
pin ownership. Diff inode 1. Cross-check candidate words against the vectors that
`mmfsckx` itself prints as `map status` and `expected`.

### 5. Indirect blocks and directories

After direct inode addressing is understood, force the transition to an indirect
block and use `tsdbfs`/`mmgetlocation` to locate both levels. Directory parsing is
a separate promotion gate: a file-data parser without a verified directory
record model is not filesystem R/O.

## Promotion gates

### Stage 1: real R/O

GPFS may move from structural inspection to filesystem R/O only when all of these
are true on at least two independently generated multi-NSD images:

- descriptor -> disk-id mapping is verified;
- inode 0 and arbitrary user inode records can be located from raw bytes;
- inode checksums are independently validated;
- direct and at least one indirect addressing level agree with
  `mmgetlocation`/`tsdbfs`;
- inode allocation state agrees with `mmfsckx`;
- directory lookup resolves paths that IBM resolves;
- sparse/data-in-inode cases are distinguished correctly;
- malformed/truncated metadata fails closed.

### Stage 2: R/W and maintenance

Writing is a much higher bar. Before `CanModify`, compact, defrag, wipe, shrink,
layout rebuild or purge is advertised, a rebuilt/mutated disposable filesystem
must pass `mmfsckx` with no metadata corruption and mount under IBM Storage Scale,
with file contents, links, xattrs/ACLs, sparse ranges and replicas verified after
remount. Allocation-map checksums, log/recovery semantics, replica updates and
all touched reserved metadata must already be understood.

Until then, a guessed writer is merely a distributed corruption primitive with
excellent throughput.

## What is deliberately still unknown

- byte offsets and widths of inode fields;
- inode checksum algorithm/coverage;
- packed representation of disk addresses and replica slots;
- indirect-block header, checksum and slot layout;
- directory block/entry format;
- block-map and inode-map record headers/checksums;
- allocation-map bit order and region-to-file-offset function;
- snapshot/COW sharing representation;
- fileset inode-space translation beyond the masks printed by IBM tools;
- log format and recovery transactions;
- exact format-version deltas.

Those are experiments, not TODO-shaped excuses to guess.

## Repository oracle model

`Hawkynt.FileFormats.FileSystems/FileSystems/FileSystem.Gpfs/GpfsOracleModel.cs` parses only diagnostic text and
represents the verified semantic facts above. Tests use published IBM examples,
including the 14-disk `mmfsckx` case, an allocation-map mismatch, a replicated
`tsdbfs` inode, `mmfileid` ownership and a three-replica `mmgetlocation` record.

That model is intentionally internal. It exists to validate future binary
parsers; it is not a user-facing promise that the raw on-disk fields are already
known.
