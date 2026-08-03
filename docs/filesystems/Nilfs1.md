# NILFS v1 (`Nilfs1`)

NILFS v1 log-structured filesystem (precursor to NILFS2) — minimal writer + reader.

> Generated from the implementation. Edit the doc comments on the descriptor,
> reader or writer rather than this file; a test regenerates it and fails on drift.

## At a glance

| | |
|---|---|
| Category | Archive |
| Family | Archive |
| Default extension | `.nilfs1` |
| Recognised extensions | `.nilfs1`, `.nilfs` |

## Detection

| Bytes | At offset | Confidence |
|---|---|---|
| `01 00 00 00 00 00 34 34` | 1024 | 0.92 |
| `34 34` | 1030 | 0.80 |

## Verbs

| Verb | Offered | What it does |
|---|---|---|
| list / extract | yes | read the volume and copy files out of it |
| create | yes | write a fresh volume holding the given files |
| add / remove | yes | change a volume in place |
| defragment | yes | lay the volume out again |
| wipe free space | yes | zero what no file holds |
| shrink | yes | reduce the volume to what it needs |
| optimise layout | yes | re-lay the volume at a chosen geometry |
| report layout | yes | say where every byte belongs |
| move blocks | no | relocate a run and repoint what names it |
| move metadata | no | relocate the volume's own structures |

### How it defragments

By moving what is out of place, through `Nilfs1BlockMover`.
A run is copied and whatever records its position is rewritten, so the cost is
the bytes that actually move rather than the whole volume.

| Property | Value | Meaning |
|---|---|---|
| Repoints runs independently | yes | whether a file in several pieces can be moved one piece at a time |
| Relinks a whole allocation | no | whether a scattered file's chain can be restated in one call |
| Holds runs outside the volume | yes | whether a full volume can be rearranged by lifting a run into memory |

## How a volume is laid out

### Nilfs1FormatDescriptor

Descriptor for NILFS v1 — the original (pre-mainline) New Implementation of a Log-structured File System, predecessor of NILFS2. Shares the 0x3434 magic with NILFS2 but is distinguished by `s_rev_level == 1` (NILFS2 uses rev≥2).

Writer scope. Per the task brief, NILFS v1's full DAT-tree / segment-usage / log-replay surface is a multi-week effort. The writer here emits a spec-compliant superblock plus a single segment with a compact directory + payload region. External NILFS v1 tools that validate the superblock signature accept the result; our reader fully round-trips List and Extract through the writer's directory marker.

Hierarchy. Subdirectories are recorded as path-prefixed entries in the writer's compact directory ('/' separator). The reader returns the flat list with subdir prefixes; consumers reconstruct the tree via `WriteFile`.

References:

### Nilfs1Reader

Reads NILFS v1 superblock metadata — the original (pre-mainline) New Implementation of a Log-structured File System. NILFS v1 was the out-of-tree precursor to NILFS2 (mainline since 2.6.30); it shares the same 0x3434 superblock magic but uses `s_rev_level == 1`.

Full DAT-tree / cpfile-driven root-dir enumeration of an arbitrary external NILFS v1 image is a multi-week effort (the cpfile inode walk and segment usage table are sparsely documented for v1 specifically) — so this reader surfaces metadata for unknown images, and reads our own writer's compact directory index when the image carries the `WriterMagic` marker right after the superblock.

Superblock layout (selected, little-endian, sits at file offset 1024): 0x00 u32 s_rev_level (== 1 for NILFS v1) 0x04 u16 s_minor_rev_level 0x06 u16 s_magic (must be 0x3434 = NILFS_SUPER_MAGIC) 0x08 u16 s_bytes 0x0A u16 s_flags 0x14 u32 s_log_block_size 0x18 u64 s_nsegments 0x20 u64 s_dev_size 0x30 u32 s_blocks_per_segment 0x38 u64 s_last_cno (last checkpoint number) 0xA8 byte[80] volume label (s_volume_name; written by Nilfs1Writer) ...

### Nilfs1Writer

Writes a minimal NILFS v1 image. Emits a fully spec-compliant superblock (NILFS_SUPER_MAGIC 0x3434 at offset 1030, s_rev_level == 1) followed by a single segment containing a compact directory index plus the file payloads.

Scope. Per docs/FILESYSTEMS.md NILFS v1 is the original out-of-tree precursor to the mainline NILFS2 driver — full DAT-tree / segment-usage walking + Linux kernel mount support is a multi-week effort that requires the (sparsely documented) pre-mainline log replay code. What we ship here is enough for round-trip List/Extract via our reader and for external tools that only validate the superblock signature.

On-disk layout.

### Nilfs1ExtentMap

Walks a NILFS v1 image (as written by `Nilfs1Writer` and mutated by `Nilfs1InPlaceModifier`) and emits its on-disk byte layout: the boot+superblock region, the base directory header + every appended log-segment header/directory as metadata-reserved, and one `Used` extent per currently-live file payload (highest-cno-per-name wins; tombstoned and superseded payloads are deliberately left uncovered so the wipe verb can reclaim/scrub them).

This live-only extent set is what makes the wipe verb forensically honest on a log-structured volume: Remove only tombstones (snapshot data stays byte-identical), and a subsequent WipeUnusedSpace zero-fills the now-dead payload bytes because they are no longer claimed by any live extent.

For images we did not write ourselves (no `WriterMagic` marker) we emit a coarse map: metadata-reserved for the boot+superblock area, free for the rest. NILFS v1's true segment-usage walk is out of scope.

The image is read through an `ImageAccessor` rather than copied in: the directories are a few kilobytes however many gigabytes of payload they describe.

### Nilfs1Layout

Finds the payloads the base segment holds and the eight bytes that say where each one starts.

A payload's position is written down as an offset from the start of the segment that describes it. For the base segment that offset is a field in its directory, and moving a payload is a change to that field — provided the payload stays inside the base segment's own area.

It has to. The reader finds the first appended segment by carrying on from where the base payloads end, and each further one from where the previous segment's payloads end; a payload that reached past a segment header would hide it, and one before its own segment's payload start is a negative offset the format cannot express.

## Parameters

| Key | Kind | Default | Allowed | Meaning |
|---|---|---|---|---|
| `BlockSize` | Enum | `4 KB` | `Auto`, `1 KB`, `2 KB`, `4 KB`, `8 KB`, `16 KB`, `32 KB`, `64 KB` | Block size in bytes — NILFS v1 supports any power of two in [1024, 65536]. |
| `Checksum` | Boolean | `false` | any | Sets s_flags bit 0 advertising that segments carry per-segment checksums (informational only — our writer does not compute them). |
| `SegmentSize` | String | `0` | any | Segment size in bytes (0 = 8 × block size, the v1 default). |
| `VolumeLabel` | String | `` | any | Volume name (16 ASCII chars, written into the spec's volume-label slot). |

## Storage methods

- `stored` — Stored

## Further reading

- https://nilfs.sourceforge.io/ — NILFS project home (covers the original NILFS v1)
- https://github.com/torvalds/linux/blob/master/include/uapi/linux/nilfs2_ondisk.h — shared on-disk superblock layout (s_rev_level discriminates v1)
- https://en.wikipedia.org/wiki/NILFS — Wikipedia article

