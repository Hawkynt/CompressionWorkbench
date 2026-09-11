# NetApp WAFL: structural read boundary and maintenance feasibility

This note records what `FileSystem.Wafl` can establish from public NetApp material, the clean-room path from Stage 0 to Stage 1, and the remaining evidence needed for namespace reads and safe mutation.

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
3. rejects zero, reserved root VBNs and out-of-image values;
4. dereferences each candidate and accepts it only when the target block begins with the same fsinfo magic;
5. recognizes a lookup-table start only when it is unique and the following slot is either zero or another verified fsinfo reference;
6. performs that work independently for both redundant volinfo roots;
7. exposes every verified fsinfo block as `fsinfo/vbn-N.bin`;
8. reports one `ActiveFsInfoVbn` only when the usable redundant roots agree, or when only one usable volinfo root remains.

If multiple plausible tables are present, the parser fails closed at Stage 0. If the two valid volinfo copies identify different active fsinfo roots, it reports both verified roots but does not arbitrarily choose one. That matters because the redundant roots can represent different consistency-point states.

This is a genuine Stage-1 read path: it follows an on-disk pointer relation and validates the target block. It is not yet a namespace reader.

## What the public material says about the next layer

### Classic WAFL inode profile

The original WAFL patents are unusually concrete for the legacy profile:

- allocation blocks are 4096 bytes;
- an on-disk inode is **128 bytes**;
- the first 64 bytes contain ordinary inode metadata;
- the final 64 bytes either hold inline file data for files up to 64 bytes or **16 × 32-bit block numbers** at a common indirection level;
- a 4 KiB inode-file data block therefore contains **32 inodes**;
- indirect blocks contain **1024 × 32-bit VBNs**;
- the fsinfo block contains the inode that roots the inode file.

US5819292 and US6289356 also describe the legacy metadata files:

- `blkmap`: one 32-bit allocation/snapshot entry per 4 KiB block;
- `inomap`: one 8-bit free-inode count per inode-file block;
- directories: 4 KiB blocks with fixed-size directory records at one end and packed variable-length names at the other; records include file ID, generation, name hash and name pointer/offset.

These facts are sufficient to implement a classic buftree engine once the byte offsets for the fsinfo inode and the inode metadata fields that identify size/type/level are established independently.

### Modern WAFL is not the same inode format

The legacy 128-byte structure must not be silently applied to modern ONTAP. Published work with NetApp authors describes a current **192-byte on-disk inode**, and FlexVol block pointers carry both virtual and physical addressing information. Modern ONTAP EMS also reports VBN/VVBN/FBN values using wide integer fields.

The implementation therefore does **not** scan arbitrary 128-byte chunks and call plausible-looking values inodes. That would turn a useful old patent into a corruption generator.

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
3. classic or modern block-pointer encoding;
4. reserved metadata/root-directory inode identities;
5. directory fixed-record width and field offsets;
6. validation/checksum fields used to reject stale or malformed blocks.

The commercial UFS Explorer implementation is useful only as a behavioural oracle: its public release notes advertise experimental WAFL metadata versions 2–4, including 32/64-bit and traditional/Flex profiles. No proprietary implementation code is used or translated here.

The independent Aaru investigation is likewise used only as a feasibility cross-check. Its author mentions unpublished reverse-engineered parsing code; that unpublished code and any ONTAP disassembly are explicitly outside this clean-room implementation.

## Why R/W and maintenance still remain disabled

The Stage-1 root walk improves read-side knowledge but does not establish block liveness. A complete modern writer must prove:

1. version-specific inode and indirect-block encodings;
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

The pseudo-archive now exposes:

- `metadata.ini` — detector and structural-probe evidence;
- `fsinfo/vbn-N.bin` — each fsinfo block reached and validated by a Stage-1 lookup-table probe;
- `wafl-volume.bin` — the complete opaque logical volume image.

Seekable sources are never copied wholesale during reader construction. Volinfo and verified fsinfo blocks are read in 4 KiB units; the raw image remains a bounded view over the caller's stream. The explicitly buffered `Extract` compatibility API materializes the raw image only when requested and refuses arrays beyond `Array.MaxLength`.

## Sources and clean-room licensing posture

Public factual/specification sources:

- NetApp ONTAP EMS `raid.vol.volinfo.mismatch`.
- Dave Hitz, James Lau, Michael Malcolm, *File System Design for an NFS File Server Appliance* (TR-3002 / USENIX).
- US5819292, consistency points/snapshots and classic WAFL structures.
- US5963962 / US6289356, classic inode, block-map, inode-map and directory architecture.
- US7313720 / US8122286, volinfo/fsinfo hierarchy and VBN lookup table.
- US7321962 / US7194595, hybrid FlexVol VBN translation and special-block handling.
- NetApp, *FlexVol: Flexible, Efficient File Volume Virtualization in WAFL*.
- NetApp, *Scalable Write Allocation in the WAFL File System*.
- Aaru issue #61, feasibility/oracle information only.
- UFS Explorer public WAFL support notes, behavioural-oracle scope only.

No NetApp binaries, disassembly, confidential material, unpublished reverse-engineering notes, or third-party WAFL parser implementation code are copied or translated. Numeric constants and structural relationships used here are public format facts required for interoperability.
