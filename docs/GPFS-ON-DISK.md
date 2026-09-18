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

`tsdbfs inode` additionally exposes physical file pointers in its
`Disk pointers [...]` section. `mmgetlocation -Y -L` independently exposes the
NSD name and GPFS disk ID for each logical file chunk, but does **not** expose a
physical sector. `mmlsdisk -L` maps those numeric disk IDs to the NSDs and their
logical sector sizes. This makes the following semantic type **verified**:

    DiskAddress = (diskId, sectorNumber)

It does **not** yet establish the byte encoding of a `DiskAddress` inside an
inode or indirect block.

Sources:

- <https://www.ibm.com/docs/en/storage-scale/5.2.3?topic=locality-mmgetlocation>
- <https://www.ibm.com/docs/en/storage-scale/6.0.0?topic=reference-mmfsckx-command>
- <https://www.ibm.com/support/pages/node/7246086>
- <https://www.ibm.com/support/pages/node/7229010>

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

A separate IBM support example for a direct inode prints a `Disk pointers [330]`
section with slot 0 at physical address `4:18497536`. This is sufficient to model
the pointer slot and its semantic disk address, but not its raw packed bytes.

The semantic models checked into `GpfsOracleModel.cs` and
`GpfsTsdbfsPointerParser.cs` therefore record:

- inode number, snapshot id and index within an inode block;
- every physical replica address of the inode record;
- inode size and address-slot count;
- indirection level and lifecycle status;
- object version, generation and link count;
- block-size code and last-block subblock count;
- checksum value plus whether the IBM tool considers it valid;
- logical file size and full-block count;
- current/maximum metadata and data replica counts;
- data-pool index;
- explicitly printed disk-pointer slot numbers and every physical replica address
  shown for those slots.

These fields are **not** assigned binary offsets yet. The tool output proves the
semantics, not their representation.

Sources:

- <https://www.ibm.com/support/pages/node/7246086>
- <https://www.ibm.com/support/pages/node/7229010>

IBM also publishes `gpfs_iattr_t`/`gpfs_iattr64_t`, whose API structures contain
an `ia_checksum` member described as a validity check on the returned attribute
structure. That is useful API context but is **not** evidence that the in-memory
API structure is the raw on-disk inode or that its checksum has the same
algorithm/coverage. Do not use that declaration as an on-disk layout shortcut.

Source:

- <https://www.ibm.com/docs/en/storage-scale/6.0.1?topic=interfaces-gpfs-iattr-t-structure>

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

That gives a raw-image experiment a three-way independent cross-check:

- `mmgetlocation`: file logical chunk -> NSD name / GPFS disk id;
- `tsdbfs inode`: inode replica and file pointer -> physical `disk:sector`;
- `mmfileid`: physical `disk:sector` -> reserved object or user inode/path.

A raw parser does not get promoted until all three directions agree with its own
answer on the same captured filesystem.

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

The demo also installs a placement policy in which `*.hot` files use the system
pool while other files use the capacity pool. The lab uses the same documented
pool topology to force a known file from capacity to system for the disk-address
packing experiment instead of hoping a generic rebalance happens to move it.

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
    mmlsnsd -f <fs> -m -Y
    mmfsckx <fs> --check-reserved-files-only
    tsdbfs <fs> inode 0
    tsdbfs <fs> inode 1
    tsdbfs <fs> inode 2
    tsdbfs <fs> inode 4
    tsdbfs <fs> inode 5
    tsdbfs <fs> inode 38

`mmfsckx` is an online checker, so collect it while the filesystem is mounted.
Only after every IBM oracle read is complete should the filesystem be synced and
cleanly unmounted for the raw image readout.

For every selected user inode also collect:

    tsdbfs <fs> inode <inode-number>
    mmgetlocation -f <path> -Y -L

For every physical address printed by the captured `tsdbfs` reports, run
`mmfileid` while the filesystem is still mounted. Preserve the GPFS disk id next
to each query because ordinary `mmfileid` output may only repeat the physical
sector. A failed lookup makes that capture incomplete; do not silently omit it.

### Controlled corpus

Build one object at a time and capture after each step:

1. deterministic anchor file;
2. empty regular file;
3. tiny file expected to remain data-in-inode;
4. exactly one subblock of non-zero deterministic data;
5. exactly one full block;
6. one full block plus one byte in the capacity pool;
7. force that same file to the system pool with `mmchattr -P system -I defer`
   and `mmrestripefile -p`, then capture its second physical placement;
8. file large enough to force one indirect level;
9. sparse file with holes at known logical offsets;
10. enough directory entries to grow its directory representation;
11. symlink and hard link;
12. file with ACL and xattr data;
13. replicated file spanning at least two NSDs;
14. delete the earlier one-subblock file and capture a stable baseline;
15. create one **empty** regular file and capture the inode allocation;
16. delete exactly that empty file and capture the inode deallocation;
17. create a second empty file and capture it as the block-map baseline;
18. write exactly one non-zero subblock to that existing inode and capture;
19. truncate that same file back to zero and capture the block deallocation.

The final transitions deliberately separate the maps. `120-inode-allocated` and
`121-inode-freed` change inode allocation without a user-data subblock.
`130-block-baseline`, `131-block-allocated` and `132-block-freed` keep the inode
allocated throughout the data allocation/free pair. Raw map diffs are restricted
to the IBM-located reserved inode-1 or inode-2 contents, so unrelated directory
or inode timestamps elsewhere cannot be mistaken for bitmap bits.

Do not use all-zero payloads as the only data corpus: sparse/zero optimizations can
make a physically empty block look like an addressing rule. Use deterministic
non-zero bytes and record SHA-256 for every file.

### Raw NSD images

The useful experiment is **paired** images: before and after exactly one semantic
change. The filesystem must be cleanly unmounted before each raw capture so an
image is internally consistent. Dump every NSD in the filesystem, not merely the
NSD named by `mmgetlocation`; metadata replicas and allocation maps can change
elsewhere.

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

### 1. Inode-number -> inode-record address and checksum

For several inodes in different inode blocks/filesets:

1. obtain physical inode replicas with `tsdbfs inode`;
2. multiply each reported sector by that NSD's `mmlsdisk -L` logical sector size
   and read exactly the reported inode-size bytes from the matching raw image;
3. find byte offsets whose values track independently known inode fields across
   controlled edits and replicas;
4. locate the literal checksum reported by `tsdbfs` under explicit endian
   hypotheses, then test candidate checksum algorithms/coverage only after the
   field offset remains stable on independent inodes;
5. reject the checksum hypothesis unless it validates a second inode and a
   second independently formatted filesystem.

IBM's public `gpfs_iattr_t` checksum is not an on-disk checksum specification and
must not be used to skip these experiments.

### 2. Disk-address byte representation

Use the same file at captures `050-block-plus-one` and `055-rebalanced`. The IBM
Vagrant placement policy starts it in capacity; the controlled transition forces
it into system. Require `mmgetlocation` to show the expected NSD/pool change and
`tsdbfs` disk pointers to show a changed physical `disk:sector`.

Search only the independently identified inode/indirect-block bytes for those two
physical addresses under explicit packing/endian hypotheses. Intersect candidates
from the paired records and reject every encoding that does not predict both
placements. `mmfileid` must map each reported physical pointer back to the
expected inode/path.

### 3. Inode allocation-map bit order

Use `110-delete` as baseline, create the empty inode at `120-inode-allocated`,
then delete exactly it at `121-inode-freed`. Record the new inode number. Diff
only the raw data belonging to reserved inode 2. Require the allocation and
deallocation transitions to reverse the same bit, then repeat across byte, word,
record, region and segment boundaries.

One bit transition cannot establish LSB-first versus MSB-first numbering; at
least two known logical indices are required before fitting the bit direction.

### 4. Block allocation-map bit order

Use `130-block-baseline` as the state where the probe inode exists but has no data
allocation. Write exactly one subblock and capture `131-block-allocated`, then
truncate the same inode to zero and capture `132-block-freed`. Use `tsdbfs`
physical disk pointers plus `mmfileid` to pin the allocated block to the expected
file, then diff only the raw data belonging to reserved inode 1. Require the
allocation/deallocation pair to reverse the predicted bit. Cross-check candidate
word orientation against vectors that `mmfsckx` itself prints as `map status`
and `expected`.

### 5. Indirect blocks and directories

After direct inode addressing is understood, use the controlled large-file
capture to force the transition to an indirect block and correlate its IBM
`tsdbfs` pointers before assigning raw offsets or checksum rules. Directory
parsing is a separate promotion gate: a file-data parser without a verified
directory record model is not filesystem R/O.

## Promotion gates

### Stage 1: real R/O

GPFS may move from structural inspection to filesystem R/O only when all of these
are true on at least two independently generated complete multi-NSD corpora with
different filesystem UIDs:

- descriptor -> disk-id mapping is verified;
- inode 0 and arbitrary user inode records can be located from raw bytes;
- inode checksum algorithm and coverage are independently validated;
- packed disk-address representation predicts controlled placement changes;
- direct and at least one indirect addressing level agree with
  `mmgetlocation`/`tsdbfs`/`mmfileid`;
- inode allocation state agrees with `mmfsckx`;
- block allocation state agrees with `mmfsckx`;
- directory lookup resolves paths that IBM resolves;
- sparse/data-in-inode cases are distinguished correctly;
- malformed/truncated metadata fails closed.

The repository's `GpfsReadOnlyPromotionGate` represents those requirements
explicitly. Synthetic fixtures test the gate itself; they cannot satisfy it.

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

`Hawkynt.FileFormats.FileSystems/FileSystems/FileSystem.Gpfs/GpfsOracleModel.cs`
parses only diagnostic text and represents verified semantic facts from IBM
output. `GpfsTsdbfsPointerParser` models only explicitly printed disk-pointer
slots; `GpfsMmfileidAggregateParser` preserves disk identity around each reverse
ownership query; and `GpfsRawCorrelation` generates representation candidates
against raw bytes without promoting them into format rules.

Tests use published IBM examples, including the 14-disk `mmfsckx` case, an
allocation-map mismatch, a replicated `tsdbfs` inode, a direct `Disk pointers`
example, `mmfileid` ownership and a three-replica `mmgetlocation` record. Optional
`ExternalFsInterop` tests consume two out-of-tree, independently formatted real
corpora through `CWB_GPFS_CORPUS_A` and `CWB_GPFS_CORPUS_B`.

These models are intentionally internal. They exist to validate a future binary
parser; they are not a user-facing promise that the raw on-disk fields are
already known.
