# HAMMER (DragonFly BSD) (`Hammer`)

HAMMER (DragonFly BSD original) filesystem image — volume header surface only. WORM emit deferred: HAMMER1 requires a real cluster B-tree (zone blockmap → cluster → inode → records with hammer_crc_t CRCs across every node), a per-volume TID generator with monotonic ordering across the whole transaction log, and a valid undo-fifo head/tail — none of which we can validate without a running DragonFly BSD instance. Multi-week effort, deferred to a future phase.

> Generated from the implementation. Edit the doc comments on the descriptor,
> reader or writer rather than this file; a test regenerates it and fails on drift.

## At a glance

| | |
|---|---|
| Category | Archive |
| Family | Archive |
| Default extension | `.hammer` |
| Recognised extensions | `.hammer` |

## Detection

| Bytes | At offset | Confidence |
|---|---|---|
| `31 30 52 C5 4D 4D 41 C8` | 0 | 0.85 |

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

By moving what is out of place, through `HammerBlockMover`.
A run is copied and whatever records its position is rewritten, so the cost is
the bytes that actually move rather than the whole volume.

| Property | Value | Meaning |
|---|---|---|
| Repoints runs independently | yes | whether a file in several pieces can be moved one piece at a time |
| Relinks a whole allocation | no | whether a scattered file's chain can be restated in one call |
| Holds runs outside the volume | yes | whether a full volume can be rearranged by lifting a run into memory |

## How a volume is laid out

### HammerFormatDescriptor

Read-only descriptor for HAMMER (DragonFly BSD original) filesystem images. Surfaces the volume header at offset 0 plus a structured metadata bundle and the raw image. Walking the HAMMER B-tree (zone blockmap → cluster → inode → records) is explicitly out of scope (multi-week effort). Magic: 8-byte uint64 `vol_signature = 0xC8414D4DC5523031` ("HAMMER01") at offset 0, serialised LE on disk as `31 30 52 C5 4D 4D 41 C8`. Confidence 0.85: an 8-byte magic value at offset 0 is high-confidence but HAMMER lacks an additional sanity check at this stage of detection (the `vol_fstype` UUID at offset 64 is not validated against a well-known constant). References:

### HammerReader

Walks a HAMMER (DragonFly BSD, HAMMER1) volume's global B-Tree and yields the regular files it contains as `path -> bytes`. This is the read side of full file support: it parses the volume header, resolves zone offsets through the freemap (zone-4 two-layer blockmap), recursively descends the B-Tree from `vol0_btree_root`, and reassembles inodes, directory entries and data records into a directory tree.

On-disk references (sys/vfs/hammer/hammer_disk.h):

### HammerWriter

Writes a single-volume HAMMER (DragonFly BSD, HAMMER1) filesystem image that DragonFly recognises and mounts. The output is a faithful port of `newfs_hammer(8)` (`sbin/newfs_hammer/newfs_hammer.c`) together with the on-disk helpers in `sbin/hammer/ondisk.c` and `sbin/hammer/blockmap.c`: it lays down the volume header, the freemap (zone-4 two-layer blockmap), the UNDO/REDO FIFO (zone-3), and a minimal root B-Tree (zone-8) holding the root directory's inode and PFS#0 records.

Geometry mirrors newfs exactly: the volume is split into a 256 KB header junk area (vol_bot_beg), a boot area, a memory log, then the zone-2 buffer area. Every metadata block carries the version-gated CRC (see `HammerCrc`).

HAMMER's UNDO FIFO has a hard minimum of HAMMER_MIN_UNDO_BIGBLOCKS (64) * HAMMER_BIGBLOCK_SIZE (8 MB) = 512 MB, so the smallest volume that newfs_hammer/this writer can format is on the order of ~1 GB. The output stream is grown to that size (sparse on filesystems that support holes).

Files passed to `AddFile` are materialised as real records readable by the DragonFly kernel: each gets a regular-file inode record, a directory-entry record under the root directory (keyed by the ALG1 directory namehash) and one or more zone-11 small-data records (payload split into 16 KB blocks, each rounded up to a power-of-two block). All records, plus the root inode and PFS#0 record, live in a single sorted leaf B-Tree node — which caps the image at HAMMER_BTREE_LEAF_ELMS (63) elements (~20 files). Files are placed flat in the root directory (no sub-directory nesting).

### HammerExtentMap

Reads a HAMMER volume's freemap and reports which bytes are in use. HAMMER allocates in 8 MB big-blocks: a layer-2 entry per big-block records which zone owns it and how far into it the allocator has appended. A big-block no zone owns is free outright, and the tail of one past its append point is free as well — which is where a removed file's bytes stay.

## Parameters

| Key | Kind | Default | Allowed | Meaning |
|---|---|---|---|---|
| `Label` | String | `` | any | Volume label (newfs_hammer -L); max 63 ASCII chars. |

## Storage methods

- `stored` — Stored

## Further reading

- https://github.com/DragonFlyBSD/DragonFlyBSD/blob/master/sys/vfs/hammer/hammer_disk.h
- https://www.dragonflybsd.org/hammer/

