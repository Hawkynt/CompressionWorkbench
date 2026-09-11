# NetApp WAFL: Stage-0 boundary and maintenance feasibility

This note records why `FileSystem.Wafl` remains read-only/opaque even though the public WAFL material is detailed enough to describe the filesystem architecture.

## Publicly established facts

The NetApp WAFL papers and patents describe a filesystem rooted at an `fsinfo` structure. The root inode points to the inode file; other metadata such as the block map and inode map is stored as files. WAFL uses 4 KiB blocks and advances the committed filesystem state through consistency points. Two `fsinfo` copies are used so that at least one committed root remains available if an update is interrupted.

These facts are sufficient for architectural validation and for reporting the fixed 4096-byte allocation unit. They are not sufficient for a byte-complete offline implementation of modern ONTAP storage.

## Missing information that blocks a safe reader/writer

A modern WAFL volume does not reduce to one flat array of 4 KiB blocks. A complete implementation needs to prove, from disk bytes alone:

1. the exact current on-disk inode and indirect-block encodings;
2. FBN/VBN/PVBN translation for the relevant ONTAP generation;
3. aggregate-to-FlexVol container mappings;
4. RAID member/stripe placement, including sector/checksum layouts used by the physical media;
5. allocation-map semantics for active data and snapshots;
6. the exact consistency-point/root selection and validation rules needed after mutation.

The independent Aaru WAFL investigation reached the same practical boundary: useful parsing requires multiple RAID-member images and unpublished/reverse-engineered details. Its issue also mentions sample reverse-engineered code that was intentionally not published. That unpublished code is **not** used here.

## Maintenance verbs

The maintenance verbs are deliberately absent rather than implemented as no-ops:

| Verb | Status | Reason |
| --- | --- | --- |
| Compact | unavailable | composite of defrag/layout/shrink; none can be proven safe |
| Defrag | unavailable | relocating a live block requires exact reverse mappings and snapshot reachability |
| Wipe | unavailable | "free" cannot be established safely without the active/snapshot allocation maps |
| Shrink | unavailable | requires proving that no live or snapshot-referenced block exists past the new end |
| Layout rewrite | unavailable | the public sources fix the 4 KiB block size, but do not define a standalone creator/rebuilder for modern aggregate/FlexVol images |
| Purge | unavailable | producing a valid empty WAFL volume requires a writer for all root metadata and allocation maps |

`ILayoutOptimizable.AnalyzeLayout` is implemented only to report the published fixed 4096-byte allocation unit. It does not make the support matrix claim `Layout`, because no rebuild implementation exists.

## Implementation and memory behaviour

The Stage-0 pseudo-archive exposes:

- `metadata.ini` — parsed Stage-0 facts and the capability boundary;
- `wafl-volume.bin` — the opaque source image.

For seekable sources the reader now keeps the source stream and reads only the eight-byte Stage-0 prefix during construction. `wafl-volume.bin` is exposed through a bounded stream, so opening or listing a multi-gigabyte/terabyte image no longer duplicates the whole image in managed memory. The explicitly buffered compatibility API still materializes the entry when requested.

## Sources and licensing approach

The implementation is clean-room with respect to implementation code: no NetApp binaries, disassembly, unpublished reverse-engineering notes, or third-party WAFL parser code were copied or translated.

Public sources used only for factual behaviour and architecture:

- Dave Hitz, James Lau, Michael Malcolm, *File System Design for an NFS File Server Appliance* (NetApp TR-3002 / USENIX, 1994/1995).
- US5819292, *Method for maintaining consistent states of a file system and for creating user-accessible read-only copies of a file system*.
- US6289356, *Write anywhere file-system layout*.
- NetApp, *Scalable Write Allocation in the WAFL File System*.
- Aaru issue #61, *Add support for NetApp WAFL filesystem*, used as independent corroboration of the multi-disk/reverse-engineering boundary only.

Patents and papers are treated as specifications/behavioural references. No expressive implementation code is taken from them.
