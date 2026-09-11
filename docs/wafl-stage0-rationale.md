# NetApp WAFL: Stage-0 boundary and maintenance feasibility

This note records what `FileSystem.Wafl` can establish from public NetApp material, corrects the repository's former synthetic detector, and explains why the format cannot honestly be promoted to R/W yet.

## Corrected detector

The previous Stage-0 implementation treated ASCII `wafd` at byte offset 0 as a WAFL FSinfo signature. A search of NetApp documentation, patents and independent implementations found no authoritative source for that value or location. It was therefore removed as a detector contract.

NetApp's current ONTAP EMS documentation provides a substantially better anchor:

- the **volinfo** block is the WAFL superblock;
- two copies are stored at filesystem **VBN 1 and VBN 2**;
- the volinfo block-type magic is **`0xdab8fbab`**.

See NetApp ONTAP EMS, `raid.vol.volinfo.mismatch`:
https://docs.netapp.com/us-en/ontap-ems/raid-vol-events.html#raid-vol-volinfo-mismatch

US7313720 describes the same VBN 1/2 volinfo placement, the volinfo/fsinfo hierarchy and the field ordering around the volinfo magic/version. It also explicitly notes that alternate embodiments may add or change fields. For that reason the reader does not invent one universal fixed byte offset for the magic within the 4 KiB block: it scans aligned 32-bit words in VBNs 1 and 2 for the documented value, accepts either byte order, and reads the immediately following volinfo-version field described by the patent.

The generic fixed-offset `MagicSignatures` list is intentionally empty. `.wafl` remains the routing extension; `WaflReader` performs the content validation.

## Publicly established layout facts

NetApp's WAFL papers and patents describe a block-based filesystem using 4 KiB blocks. The volinfo root references fsinfo blocks; fsinfo leads to the inode file; the inode file contains ordinary and metadata-file inodes. The metadata files include allocation maps, and persistent consistency point images/snapshots retain older reachable trees.

Useful public references include:

- Dave Hitz, James Lau, Michael Malcolm, *File System Design for an NFS File Server Appliance* (NetApp TR-3002 / USENIX, 1994/1995).
- US5819292, *Method for maintaining consistent states of a file system and for creating user-accessible read-only copies of a file system*.
- US6289356, *Write anywhere file-system layout*.
- US7313720, *Technique for increasing the number of persistent consistency point images in a file system*.
- NetApp, *Scalable Write Allocation in the WAFL File System*.
- NetApp ONTAP EMS `raid.vol.volinfo.mismatch` documentation cited above.

These are enough for Stage-0 validation of a **flat logical VBN image**. They are not enough to turn physical ONTAP RAID members into such an image or to traverse a modern FlexVol safely.

## Missing information that blocks safe R/W

A complete modern implementation has to prove, from disk bytes alone:

1. the exact inode and indirect-block encodings for the ONTAP generation being parsed;
2. FBN/VBN/PVBN translation and aggregate-to-FlexVol container mappings;
3. RAID member/stripe placement and any physical-sector/checksum framing;
4. active/summary/space/owner-map semantics and their versioning;
5. allocation reachability across the active filesystem and every retained snapshot;
6. consistency-point/root selection, checksums and commit ordering after mutation.

NetApp's own `wafliron` documentation reinforces the coupling: on an aggregate it checks aggregate metadata first and then all associated FlexVol volumes; it cannot simply repair an individual FlexVol in isolation.

The independent Aaru WAFL investigation reaches the same practical boundary: useful parsing requires multiple RAID-member images plus reverse-engineered details. That issue mentions sample reverse-engineered code which was intentionally not published. That unpublished code is **not** used here.

## Maintenance verbs

The maintenance verbs remain absent rather than being implemented as optimistic no-ops:

| Verb | Status | Reason |
| --- | --- | --- |
| Compact | unavailable | composite operation; the underlying relocation/rebuild steps cannot be proven safe |
| Defrag | unavailable | moving a live block requires exact reverse mappings and snapshot reachability |
| Wipe | unavailable | a block cannot be called free until active and snapshot allocation maps agree |
| Shrink | unavailable | requires proving no reachable block exists beyond the new end |
| Layout | unavailable | a new valid WAFL/FlexVol/aggregate image requires a complete creator and commit/checksum rules |
| Purge | unavailable | producing a valid empty volume requires rebuilding root metadata and allocation maps |

The fixed 4096-byte WAFL allocation unit is documented as a format fact, but reporting that fact is not the same thing as supporting the repository's `Layout` verb, which means re-laying the filesystem at a requested geometry.

## Streaming behaviour

The Stage-0 pseudo-archive exposes:

- `metadata.ini` — detector results, volinfo-copy status and the capability boundary;
- `wafl-volume.bin` — the opaque logical volume image.

Seekable inputs are no longer copied wholesale during reader construction. Only the two 4 KiB volinfo blocks are read for validation, while `wafl-volume.bin` is exposed through a bounded view over the caller's stream. The explicitly buffered `Extract` compatibility API still materializes the raw entry when requested and refuses arrays larger than `Array.MaxLength`.

## Licensing / clean-room approach

No NetApp binaries, disassembly, unpublished reverse-engineering notes or third-party WAFL parser implementation code were copied or translated. Patents and public documentation are used only for factual on-disk behavior, constants and interoperability constraints. The Aaru issue is used only as an independent feasibility cross-check.
