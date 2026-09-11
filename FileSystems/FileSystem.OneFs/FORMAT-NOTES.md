# Dell PowerScale / Isilon OneFS raw-media notes

## Scope

`FileSystem.OneFs` is an **offline raw-image** handler. It is not a client for a
running PowerScale cluster and must not silently substitute OneFS REST, SMB, NFS
or SSH operations for the repository's `Stream`-based image contracts.

The September 2026 review found substantially more public structure than the
original Stage-0 stub assumed, but still not enough byte-level serialization to
reconstruct or mutate a standalone OneFS data drive safely.

## What is publicly established

Dell's own architecture material establishes these invariants:

- OneFS presents a **single global namespace across the cluster**.
- Files and directories are identified by 64-bit LINs (logical inode numbers).
- The **superblock exists at multiple fixed block addresses on every drive**.
  It contains addresses for the LIN master, which leads to the root of the LIN
  B+ tree. Dell publishes this traversal concept but not the fixed block numbers
  or byte layout of the superblock.
- The LIN B+ tree maps LINs to **mirrored inode disk addresses**. `isi get`
  examples render an inode address as a node, logical drive, byte/block address
  and inode size, for example `1,9,1054720:512`.
- File IFM/metatrees map logical file blocks into **protection groups**; directory
  DFM trees hold names and namespace relationships.
- Data addresses shown by `isi get -DD` use the same distributed shape and can
  identify sparse regions as `0,0,0:8192`.
- Each data disk is assigned a GUID and logical drive number and is divided into
  **32 MiB cylinder groups** consisting of **8 KiB filesystem blocks**. That is
  4096 filesystem blocks per cylinder group.
- Every cylinder group tracks, with a bitmap, whether blocks are used for file
  data, inodes, or other metadata. Public material does not define the bitmap's
  raw offset or serialization.
- Layout is managed by BAM (Block Allocation Manager); local disk allocation is
  delegated to the LBM (Local Block Manager), while remote block operations go
  through the RBM.
- Safe writes are distributed transactions. BAM Safe Write builds a write plan,
  then OneFS uses two-phase commit across participating nodes and their journals.
  A correct offline writer therefore cannot update a single drive in isolation
  and pretend cluster consistency was preserved.
- OneFS is based on UFS / FreeBSD heritage, but its `/ifs` storage layer replaces
  or extends the ordinary standalone-UFS allocation model with distributed
  BAM/LBM/RBM addressing and protection. UFS ancestry is useful historical
  context, not a license to route an isolated OneFS data disk through `UfsReader`.

## `newfs_efs` and the UFS-looking format logs

A deeper search found old reimage logs where `newfs_efs` prints classic BSD-newfs
style geometry such as an 8 KiB block size, 1 KiB fragments, cylinder groups and
backup-superblock locations. That specific log formats `/dev/imdd5a`, however:
IMDD is OneFS's **Mirrored Device Driver for system volumes** such as root/`/var`,
not proof that an `/ifs` data disk is a normal UFS volume.

Separately, Dell's reimage documentation says `isi_initial_newfs` then formats the
`/ifs` partition on all drives and announces `Proceeding with v16 newfs...` while
creating a linked journal. Combined with Dell's documented 32 MiB/8 KiB data-disk
geometry, this confirms substantial BSD/UFS lineage in the formatter and physical
organization, but it still does not publish the proprietary `/ifs` superblock,
allocation bitmap, LIN tree or transaction record layouts.

The practical conclusion is narrower than the old stub's wording: **OneFS is
UFS-derived, but an isolated OneFS data drive is not established as a complete,
generic UFS filesystem image.**

## Patent-level structural evidence

Several Isilon patents predate Dell's acquisition and describe implementation
behavior in enough detail to sharpen the model without supplying a usable raw
serialization:

- US8214400B2 / US7797283 describes a distributed mirrored index tree. A
  superblock contains a header plus block-address (`baddr`) pointers to copies of
  the tree root. The header includes version/tree-height information; unused
  pointers may be zero. Inner nodes keep sorted keys/offsets at one end and
  mirrored child pointers at the other, leaving free space between them.
- US7937421B2 / application 20040153479 describes BAM layout selection and names
  `ifs_lbn_t`, `ifs_devid_t` and `ifs_baddr_t`; disk/block allocation itself is
  delegated to LBM.
- US7743033 describes BAM as working with and/or instead of BSD UFS, which is
  consistent with Dell's later description of OneFS as UFS-based while using a
  distributed allocation layer.

These patents are used only as behavioral/specification evidence. Their
pseudocode and expressive implementation are **not copied or translated**.

## Other behavioral oracles found

Public Dell command output gives useful consistency checks for a future reader:

- `isi get -D` exposes inode version, directory version, inode revision, mirror
  count, parent LIN/hash, physical-block counts and rendered IFS inode addresses.
- `isi get -DD` exposes protection groups and distributed physical data addresses.
- Dell documents OneFS NFS file-handle chunks as little-endian; a file handle can
  carry the 64-bit LIN. This is evidence about an exported protocol encoding, not
  enough to assume every raw-disk integer uses the same encoding.
- OneFS release/recovery logs contain `Bad Magic in superblock`, proving a
  superblock magic check exists in at least relevant OneFS metadata/journal paths.
  The expected value and authoritative raw-drive offset remain unverified.
- Commercial recovery reports describe scanning **all drive images** for OneFS
  inode and directory-entry copies, then combining those results cluster-wide.
  That independently reinforces the multi-drive reconstruction requirement.

A public GitHub repository named `jji0717/cp-migrate` contains source that refers
to proprietary OneFS headers such as `ifs/ifs_types.h`, `ifs/ifm/ifm_dinode.h`
and BAM APIs. The repository declares **no license** and does not contain the
missing proprietary header definitions. It is therefore treated only as a source
of behavioral/symbol-name clues; no code, layout or expressive structure from it
is reused here.

## Genuine-media oracle path

Dell publicly distributes a OneFS Simulator specifically for testing. Dell's own
community linked the 9.5.0.0 simulator ZIP, and the simulator installation guide
states that the ZIP contains an OVA. Independent inspection of that OVA reports a
small sparse appliance with 22 VMDKs: one 16 GiB system disk, one 512 MiB device,
and twenty 5 GiB SCSI data disks. Those data VMDKs are the best available oracle
for the next raw-format step because they are genuine Dell-authored OneFS media,
not synthetic bytes invented by this repository.

The intended clean-room oracle procedure is:

1. obtain the simulator from Dell's own download channel;
2. extract the OVA/VMDKs without redistributing them;
3. identify which 5 GiB VMDKs are initialized `/ifs` data drives;
4. reconstruct each sparse VMDK as logical raw sectors using this repository's
   existing VMDK reader;
5. compare the same 8 KiB block positions across many drives and fresh simulator
   clusters to find the repeated fixed-address superblock candidates;
6. change one controlled cluster property at a time, then diff those candidates;
7. validate any inferred magic/version/root-address fields against live `isi get`,
   cluster upgrade behavior and more than one simulator version before adding a
   detector or parser.

This run could verify the public simulator packaging and disk inventory, but the
execution environment could not resolve `dl.dell.com`, so no simulator bytes were
used and no claim about the missing raw offsets or magic is made here.

## What remains unknown at byte level

No authoritative publication or independently licensed implementation found in
the review defines enough raw serialization for safe traversal or mutation. In
particular, the review still does **not** establish:

- a fixed offset-zero `"OneFS"` or `"ONEF"` drive signature;
- the actual superblock magic, exact fixed superblock block addresses, checksum,
  header size, field offsets or version compatibility rules;
- the LIN-master record serialization;
- inode and B+/B* tree record encodings across supported OneFS generations;
- the cylinder-group allocation bitmap location, width, bit meanings or update
  rules;
- protection-group/parity record serialization needed to reconstruct or relocate
  file content;
- journal and two-phase-commit record formats needed to make crash-consistent
  writes;
- an offline single-drive checker that can act as an independent write oracle.

The previous descriptor's `"OneFS"` / `"ONEF"` offset-zero magic therefore still
has no acceptable reference and remains removed.

## Capability decision

| Capability | State | Reason |
| --- | --- | --- |
| list / extract | limited | synthetic metadata + byte-exact raw-image stream only |
| geometry analysis | **supported** | Dell fixes blocks at 8 KiB and groups at 32 MiB; no payload parsing required |
| create | blocked | no public raw-media writer specification/oracle |
| modify / R/W | blocked | namespace, allocation, protection and journal state are cluster-wide |
| purge | blocked | cannot remove live namespace entries and atomically update distributed metadata |
| wipe | blocked | bitmap location/encoding is unknown, so no raw block can be proven free safely |
| defrag | blocked | relocation requires IFM/protection-group/allocation updates and distributed commit |
| shrink | blocked | no proven tail ownership or supported smaller physical geometry |
| layout rebuild | blocked | fixed geometry is known; a valid alternate creator/rebuilder is not |
| compact | blocked | composite of defrag/layout/shrink, all rewriting steps remain blocked |

`OneFsFormatDescriptor` therefore implements `ILayoutOptimizable` **only for
analysis**. `AnalyzeLayout` reports the documented 8 KiB unit and 32 MiB cylinder
group. It does not estimate unknown slack and does not override `RebuildStreaming`.
The support matrix consequently keeps Layout and Compact unadvertised.

This is deliberately stricter than implementing no-op maintenance interfaces:
the support matrix treats an advertised rewriting interface as a working verb.
Returning success while changing nothing—or guessing that silent regions are
free—would be operationally dishonest and potentially destructive.

## References

Primary / official material:

- Dell Technologies Info Hub, **OneFS Metadata**:
  https://infohub.delltechnologies.com/en-us/p/onefs-metadata/
- Dell Technologies Info Hub, **OneFS architectural overview** (32 MiB cylinder
  groups, 8 KiB blocks, allocation bitmaps, BAM/LBM/RBM, safe writes):
  https://infohub.delltechnologies.com/en-us/l/high-availability-and-data-protection-with-dell-powerscale-scale-out-nas/onefs-architectural-overview-1/1/
- Dell Technologies Info Hub, **OneFS data inlining / performance** (fixed-address
  superblocks and LIN-master traversal):
  https://infohub.delltechnologies.com/en-us/l/dell-powerscale-onefs-data-reduction-and-storage-efficiency/performance-854/
- Dell PowerScale OneFS Technical Overview, **File system structure**:
  https://infohub.delltechnologies.com/en-nz/l/dell-powerscale-onefs-technical-overview/file-system-structure/1/
- Dell PowerScale reimage procedure, `isi_initial_newfs` / `/ifs` formatting
  behavior (Dell-hosted/support material; mirrored copies may require support
  access depending on region/account).
- Dell KB, **How to obtain a LIN from an NFS file handle** (little-endian exported
  LIN encoding):
  https://www.dell.com/support/kbdoc/en-us/000019498/isilon-onefs-how-to-obtain-a-lin-from-an-nfs-file-handle
- Dell OneFS Simulator Installation Guide:
  https://www.dell.com/support/manuals/en-us/isilon-onefs/ifs_pub_onefs_simulator_guide/installing-onefs-simulator
- Dell community, **Upgrading OneFS Simulator** (Dell-hosted 9.5 simulator link):
  https://www.dell.com/community/en/conversations/isilon/upgrading-onefs-simulator/647f8bdcf4ccf8a8dec07295

Implementation-independent patent evidence:

- US8214400B2, **Systems and methods for maintaining distributed data**:
  https://patents.google.com/patent/US8214400B2/en
- US7937421B2, **Systems and methods for restriping files in a distributed file
  system**:
  https://patents.google.com/patent/US7937421B2/en
- US7743033, **Systems and methods for providing a distributed file system
  utilizing metadata to track information about data stored throughout the
  system**.

## Licensing / implementation provenance

Dell documentation and patents are used only to determine factual architecture,
public behavior, constants/geometry and acceptance boundaries. Patent pseudocode
is not copied or translated. The unlicensed `cp-migrate` repository is not a code
source. Dell simulator binaries are not committed or redistributed; when
available they are suitable only as a behavioral/raw-media oracle. No external
implementation code, comments, naming structure or control flow is reproduced.
The implementation remains original managed C# under this repository's license,
with no new dependency.
