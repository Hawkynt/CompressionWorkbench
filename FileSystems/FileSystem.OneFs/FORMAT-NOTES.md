# Dell PowerScale / Isilon OneFS raw-media notes

## Scope

`FileSystem.OneFs` is an **offline raw-image** handler. It is not a client for a
running PowerScale cluster and must not silently substitute OneFS REST, SMB, NFS
or SSH operations for the repository's `Stream`-based image contracts.

The implementation review in September 2026 found enough public information to
model the architecture, but not enough to reconstruct or mutate a standalone
OneFS drive image safely.

## Publicly documented facts

Dell's published material establishes the following relevant invariants:

- OneFS presents a **single global namespace across the cluster**.
- Files and directories are identified by LINs (logical inode numbers).
- The LIN B+ tree maps LINs to **mirrored inode disk addresses**. Dell's own
  examples identify a node, drive number, physical block address and inode size.
- File IFM/metatrees map logical block numbers to **protection groups**; directory
  DFM trees hold names and namespace relationships.
- Inodes and B+ tree blocks are mirrored; the LIN root is especially heavily
  mirrored.
- File data is divided into 8 KiB filesystem blocks and distributed across the
  cluster according to protection/layout policy.
- Dell describes OneFS as UFS-based, but the data filesystem is distributed
  across nodes. UFS ancestry is therefore not evidence that a single `/ifs`
  drive can be parsed or rewritten as a normal standalone UFS volume.

## What could not be established

No Dell publication or independently verifiable implementation found during the
review defined enough of the raw-media serialization to support safe offline
mutation. In particular, the review did **not** establish:

- a fixed offset-zero `"OneFS"` or `"ONEF"` drive signature;
- the complete superblock/LIN-master serialization and version rules;
- inode and B+ tree record encodings across supported OneFS generations;
- free-space/allocation metadata sufficient to distinguish proven-dead blocks;
- protection-group membership and parity/update rules sufficient to relocate or
  replace file data;
- journal/transaction ordering required to leave an edited cluster consistent;
- an offline single-drive checker that can act as an independent write oracle.

The previous descriptor's `"OneFS"` / `"ONEF"` magic therefore had no acceptable
reference and has been removed rather than promoted into more code.

## Capability decision

| Capability | State | Reason |
| --- | --- | --- |
| list / extract | limited | synthetic metadata + byte-exact raw-image stream only |
| create | blocked | no public raw-media writer specification/oracle |
| modify / R/W | blocked | namespace, allocation and protection state are cluster-wide |
| purge | blocked | cannot remove live namespace entries and update protection metadata safely |
| wipe | blocked | cannot prove which omitted/raw blocks are unused; guessing is destructive |
| defrag | blocked | relocation requires allocation + IFM/protection-group updates |
| shrink | blocked | no proven tail/free-space ownership or canonical smaller geometry |
| layout | blocked | no proven creator/rebuilder for alternate geometry |
| compact | blocked | composite of defrag/layout/shrink, all blocked above |

This is deliberately stricter than implementing a no-op maintenance interface:
the support matrix treats an advertised interface as a working verb. Reporting a
successful wipe that merely writes zero bytes would be technically callable but
operationally dishonest.

## References

- Dell Technologies Info Hub, **OneFS Metadata**:
  https://infohub.delltechnologies.com/en-us/p/onefs-metadata/
- Dell PowerScale OneFS Technical Overview, **File system structure**:
  https://infohub.delltechnologies.com/en-nz/l/dell-powerscale-onefs-technical-overview/file-system-structure/1/
- Dell PowerScale OneFS 9.15 Administration Guide, **Structure of the file system**:
  https://www.dell.com/support/manuals/en-us/isilon-onefs/ifs_pub_91500_administration_guide_gui/structure-of-the-file-system
- Dell PowerScale OneFS Technical Specifications Guide, **File system guidelines**
  (8 KiB block size):
  https://www.dell.com/support/manuals/en-us/isilon-onefs/ifs_pub_onefs_tech_spec_guide_9.9.0.0/file-system-guidelines

## Licensing / implementation provenance

The sources above are used only for factual behavior and architecture. No Dell
implementation source, comments, naming, control flow or other expressive code
was copied or translated. The implementation is an original clean-room C#
inspection surface under this repository's license.
