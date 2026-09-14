# NetApp WAFL: structural read boundary and maintenance feasibility

This note records what `FileSystem.Wafl` can establish from public NetApp material, the clean-room path from Stage 0 to Stage 1, the classic block-tree traversal now implemented as the next Stage-2 prerequisite, and the remaining evidence needed for namespace reads and safe mutation.

## Corrected detector

The original implementation treated ASCII `wafd` at byte offset 0 as a WAFL fsinfo signature. A search of NetApp documentation, patents and independent implementations found no authoritative source for that value or location, so it was removed.

NetApp's ONTAP EMS documentation provides an authoritative root anchor:

- **volinfo** is the WAFL superblock;
- redundant copies are stored at filesystem **VBN 1 and VBN 2**;
- the volinfo block-type magic is **`0xdab8fbab`**.

See NetApp ONTAP EMS `raid.vol.volinfo.mismatch`:
https://docs.netapp.com/us-en/ontap-ems/raid-vol-events.html#raid-vol-volinfo-mismatch

US7313720/US8122286 describe the same VBN 1/2 placement and volinfo structure. The published layout allows fields to evolve, so the Stage-0 detector does not invent one universal byte offset for the volinfo magic: it scans aligned 32-bit words in the two 4 KiB roots, accepts either byte order and reads the immediately following volinfo-version field.

The generic fixed-offset `MagicSignatures` list therefore remains empty. `.wafl` is a routing extension; `WaflReader` performs content validation.

## Stage 1: volinfo → fsinfo

The same NetApp patents disclose several stronger invariants:

1. volinfo starts with backward-compatible **fsinfo magic** and **fsinfo version** fields;
2. volinfo contains a **VBN lookup table**;
3. table entry **0** points to the active filesystem's fsinfo block;
4. subsequent entries point to persistent consistency-point/snapshot fsinfo roots;
5. the illustrative direct table carries the active root plus up to 255 PCPIs.

That is enough for a real structural traversal without knowing the VBN-array byte offset.

For the disclosed **classic 32-bit direct-fsinfo profile**, `WaflReader` now:

1. derives the fsinfo compatibility magic from the start of each valid volinfo copy;
2. scans only aligned 32-bit words after the recognized volinfo header;
3. rejects zero, reserved root VBNs and out-of-image values for fsinfo-table candidates;
4. dereferences each candidate and accepts it only when the target block begins with the same fsinfo magic;
5. recognizes a lookup-table start only when it is unique and the following slot is either zero or another verified fsinfo reference;
6. performs that work independently for both redundant volinfo roots;
7. exposes every verified fsinfo block as `fsinfo/vbn-N.bin`;
8. reports one `ActiveFsInfoVbn` only when the usable redundant volinfo roots agree, or when only one usable volinfo root remains.

If multiple plausible tables are present, the parser fails closed at Stage 0. If the two valid volinfo copies identify different active fsinfo roots, it reports both verified roots but does not arbitrarily choose one. That matters because the redundant roots can represent different consistency-point states.

This is a genuine Stage-1 read path: it follows an on-disk pointer relation and validates the target block. It is not yet a namespace reader.

## Classic WAFL inode and block-tree profile

The original WAFL patents are unusually concrete for the legacy profile:

- allocation blocks are 4096 bytes;
- an on-disk inode is **128 bytes**;
- the first 64 bytes contain ordinary inode metadata;
- the final 64 bytes either hold inline file data for files up to 64 bytes or **16 × 32-bit block numbers** at a common indirection level;
- a 4 KiB inode-file data block therefore contains **32 classic 128-byte inodes**;
- a 4 KiB indirect block contains **1024 × 32-bit VBNs**;
- level 1 uses the inode's 16 VBNs directly, level 2 points to single-indirect blocks and level 3 points to double-indirect blocks;
- the fsinfo block contains the inode that roots the inode file.

US5819292 and US6289356 also describe the legacy metadata files:

- `blkmap`: one 32-bit allocation/snapshot entry per 4 KiB block;
- `inomap`: one 8-bit free-inode count per inode-file block;
- directories: 4 KiB blocks with fixed-size directory records at one end and packed variable-length names at the other; records include file ID, generation, name hash and name pointer/offset.

### Implemented classic block-tree primitive

`WaflClassicBlockTree` now implements the byte-level mechanics that are actually specified for that 128-byte profile without inventing the still-missing inode metadata offsets:

- level-0 inline data is copied from the published final 64-byte inode area;
- levels 1, 2 and 3 traverse direct, single-indirect and double-indirect 32-bit VBN trees respectively;
- the caller supplies the already-decoded inode level and logical block count, so this helper does not guess where those fields live;
- sparse-hole recognition is also caller supplied rather than assigning special meaning to pointer value zero;
- traversal is lazy and bounded by the caller's logical size;
- both byte orders are supported because the enclosing Stage-1 profile already establishes byte order;
- malformed trees are rejected for out-of-range VBNs, non-4-KiB indirect blocks, impossible level capacity and indirect-block cycles.

The explicit sparse-pointer policy matters: public NetApp patent material describes WAFL volume block numbering as beginning at VBN 0 in at least one disclosed profile. Current NetApp documentation confirms sparse files exist, but the sources reviewed here do not publish one universal on-disk hole sentinel. The decoder therefore treats zero as an ordinary in-range VBN unless a proven profile supplies a predicate identifying that value as a hole.

That removes the classic block-tree algorithm itself from the Stage-2 blocker list. It does **not** make arbitrary 128-byte windows inside fsinfo into trustworthy inode records. The root-inode placement and its metadata fields still need an independently verified byte layout before `WaflReader` can bind the helper to an image automatically.

## Inode generations: do not conflate classic and modern WAFL

The earlier rationale incorrectly described 192 bytes as the current ONTAP inode size. NetApp's current public documentation is explicit:

- the historical patent profile discussed above uses a **128-byte** on-disk inode;
- Data ONTAP releases **earlier than 9.0 use 192-byte inodes**;
- **ONTAP 9 uses 288-byte inodes**.

See NetApp KB, *What are the ONTAP limitations on files, directories, and subdirectories?*:
https://kb.netapp.com/on-prem/ontap/Ontap_OS/OS-KBs/What_are_the_ONTAP_limitations_on_files_directories_and_subdirectories

NetApp's separate *What is an inode?* article likewise states that current ONTAP inodes use 288 bytes and that files smaller than 64 bytes can live in the inode itself:
https://kb.netapp.com/on-prem/ontap/Ontap_OS/OS-KBs/What_is_an_inode

The implementation therefore does **not** apply the 128-byte patent layout to modern ONTAP, nor does it treat the 192-byte pre-9 profile as the current format. Modern FlexVol block pointers also carry virtual/physical addressing information, so the classic 32-bit VBN decoder is a specifically named profile rather than a universal WAFL parser.

## Aggregate and FlexVol boundary

Public FlexVol material gives the high-level mapping:

- an aggregate uses PVBN addressing and has its own volinfo/fsinfo/inode-file tree;
- aggregate metadata includes owner, active, summary and space maps plus a hidden metadata namespace;
- FlexVol volumes live through container-file mappings and use VVBN/PVBN dual block references;
- each FlexVol has its own volinfo/fsinfo/inode-file/root structure.

A flat logical VBN image is therefore a useful Stage-1 target, but a raw member disk is not equivalent to one. Physical reconstruction also needs RAID geometry, member ordering, stripe/parity/checksum framing and aggregate ownership information.

## Remaining blockers before Stage 2 namespace reads

A defensible file walker still needs at least one versioned profile with independently verified byte offsets for:

1. the inode-of-inode-file inside fsinfo;
2. inode type, logical size and indirection-level fields;
3. a reliable profile/version discriminator before choosing classic 128-byte, pre-ONTAP-9 192-byte or ONTAP-9 288-byte inode decoding;
4. modern/FlexVol block-pointer encoding where the image is not the classic flat-VBN profile;
5. sparse/hole pointer semantics for any profile where sparse reconstruction is required;
6. reserved metadata/root-directory inode identities or another proved way to locate the namespace root;
7. directory fixed-record width and field offsets;
8. validation/checksum fields used to reject stale or malformed blocks.

The commercial UFS Explorer implementation is useful only as a behavioural oracle: its public release notes advertise experimental WAFL metadata versions 2–4, including 32/64-bit and traditional/Flex profiles. No proprietary implementation code is used or translated here.

The independent Aaru investigation is likewise used only as a feasibility cross-check. Its author mentions unpublished reverse-engineered parsing code; that unpublished code and any ONTAP disassembly are explicitly outside this clean-room implementation.

## Why R/W and maintenance still remain disabled

The Stage-1 root walk and classic block-tree decoder improve read-side knowledge but do not establish block liveness. A complete modern writer must prove:

1. version-specific inode and directory encodings;
2. FBN/VBN/VVBN/PVBN translation and aggregate-to-FlexVol container mappings;
3. active/summary/space/owner-map semantics and versioning;
4. allocation reachability across the active filesystem and every retained snapshot;
5. consistency-point root selection, checksums and commit ordering;
6. physical RAID placement when operating below the logical-volume layer.

NetApp's own `wafliron` documentation reinforces this coupling: aggregate metadata and associated FlexVol volumes are checked as a coordinated structure rather than as unrelated standalone filesystems.

| Verb | Status | Reason |
| --- | --- | --- |
| Compact | unavailable | composite operation; underlying relocation/rebuild cannot yet be proven safe |
| Defrag | unavailable | moving a block requires every active/snapshot reference to be repointed correctly |
| Wipe | unavailable | a block is not free merely because the active root no longer references it |
| Shrink | unavailable | requires proving no active or snapshot root can reach the truncated tail |
| Layout | unavailable | needs a complete versioned creator plus CP/checksum commit rules |
| Purge | unavailable | needs valid empty root metadata and allocation maps |

No maintenance interface is added merely to make the matrix greener. Green corruption is still corruption.

## Streaming behaviour

The pseudo-archive currently exposes:

- `metadata.ini` — detector and structural-probe evidence;
- `fsinfo/vbn-N.bin` — each fsinfo block reached and validated by a Stage-1 lookup-table probe;
- `wafl-volume.bin` — the complete opaque logical volume image.

Seekable sources are never copied wholesale during reader construction. Volinfo and verified fsinfo blocks are read in 4 KiB units; the raw image remains a bounded view over the caller's stream. The explicitly buffered `Extract` compatibility API materializes the raw image only when requested and refuses arrays beyond `Array.MaxLength`.

The classic block-tree helper is intentionally not exposed as additional pseudo-files yet: until the fsinfo root inode and its size/level metadata can be bound from documented byte offsets, doing so would turn a correct traversal primitive into a heuristic parser.

## Sources and clean-room licensing posture

Public factual/specification sources:

- NetApp ONTAP EMS `raid.vol.volinfo.mismatch`.
- NetApp KB, *What are the ONTAP limitations on files, directories, and subdirectories?*.
- NetApp KB, *What is an inode?*.
- Dave Hitz, James Lau, Michael Malcolm, *File System Design for an NFS File Server Appliance* (TR-3002 / USENIX).
- US5819292, consistency points/snapshots and classic WAFL structures.
- US5963962 / US6289356, classic inode, block-map, inode-map and directory architecture.
- US7313720 / US8122286, volinfo/fsinfo hierarchy and VBN lookup table.
- US7321962 / US7194595, hybrid FlexVol VBN translation and special-block handling.
- EP1875393 / related sparse-volume material, VBN numbering behaviour only.
- NetApp, *FlexVol: Flexible, Efficient File Volume Virtualization in WAFL*.
- NetApp, *Scalable Write Allocation in the WAFL File System*.
- Aaru issue #61, feasibility/oracle information only.
- UFS Explorer public WAFL support notes, behavioural-oracle scope only.

No NetApp binaries, disassembly, confidential material, unpublished reverse-engineering notes, or third-party WAFL parser implementation code are copied or translated. Numeric constants and structural relationships used here are public format facts required for interoperability.
