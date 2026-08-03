# F2FS (`F2fs`)

F2FS flash-friendly filesystem image (R/W via log-structured append; full NAT/SIT block rewrite + regular dentry blocks on overflow)

> Generated from the implementation. Edit the doc comments on the descriptor,
> reader or writer rather than this file; a test regenerates it and fails on drift.

## At a glance

| | |
|---|---|
| Category | Archive |
| Family | Archive |
| Default extension | `.f2fs` |
| Recognised extensions | `.f2fs` |

## Detection

| Bytes | At offset | Confidence |
|---|---|---|
| `10 20 F5 F2` | 1024 | 0.95 |

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

By moving what is out of place, through `F2fsBlockMover`.
A run is copied and whatever records its position is rewritten, so the cost is
the bytes that actually move rather than the whole volume.

| Property | Value | Meaning |
|---|---|---|
| Repoints runs independently | yes | whether a file in several pieces can be moved one piece at a time |
| Relinks a whole allocation | no | whether a scattered file's chain can be restated in one call |
| Holds runs outside the volume | yes | whether a full volume can be rearranged by lifting a run into memory |

## How a volume is laid out

### F2fsFormatDescriptor

References:

### F2fsReader

Reads F2FS filesystem images using the on-disk layout defined by the Linux kernel header `include/linux/f2fs_fs.h`. Handles both traditional data-block dentries and inline dentries (i_inline F2FS_INLINE_DENTRY flag).

### F2fsWriter

Builds spec-compliant F2FS filesystem images that are accepted by Linux `fsck.f2fs`.

Layout (4 KiB blocks, 512 blocks per 2 MiB segment, single-segment sections, single-section zones):

The main area holds, in order, the populated regions sized to their actual block counts — HOT_NODE (root inode), WARM_NODE (subdirectory + file inodes), HOT_DATA (directory dentry data blocks), WARM_DATA (file data blocks) — followed by six reserved, empty "current" segments (one per CURSEG_* type). Every written block therefore lives in an ordinary, non-current segment whose owner is recorded in the on-disk SSA, and the checkpoint's cur_*_blkoff are all zero. This keeps fsck's two summary sources (the checkpoint for current segments, the SSA for everything else) from ever disagreeing.

Small directories use inline dentries (F2FS_INLINE_DENTRY) embedded in the inode at i_addr[1] (offset 364). Larger directories spill into regular 4 KiB dentry data blocks organised by the kernel's multi-level hash-bucket scheme (see PlanHashBucketDentries): a name lands in bucket hash % dir_buckets(level) at the lowest level whose target bucket has room, so fsck.f2fs's f2fs_check_dirent_position agrees with where each name is stored.

SIT entries (written for every main segment) encode the valid-block count (low 10 bits) and the segment type (high 6 bits); the SSA footer entry_type classifies each segment as node or data. fsck cross-checks all of these against the reachable inode/dentry tree.

### F2fsLayout

Finds, for every data block of every file, the four bytes that name it.

A block's address lives in the inode's own array of them, or in a direct node one, two or three levels below it. The reader walks that to read a file; this walks it to write one down — which is what a move needs and reading never does.

## Parameters

| Key | Kind | Default | Allowed | Meaning |
|---|---|---|---|---|
| `ImageSize` | Enum | `Auto (fit to files)` | `Auto (fit to files)`, `64 MB`, `128 MB`, `256 MB`, `512 MB`, `1 GB`, `2 GB` | Total image capacity. Auto sizes the image to exactly hold the files (recommended). |
| `VolumeLabel` | String | `` | any | Volume name shown by file managers (max 16 chars). |

## Storage methods

- `stored` — Stored

## Further reading

- https://docs.kernel.org/filesystems/f2fs.html — Linux kernel F2FS documentation (on-disk layout: SB/CP/SIT/NAT/SSA/main area)
- https://www.usenix.org/conference/fast15/technical-sessions/presentation/lee — Lee et al., "F2FS: A New File System for Flash Storage" (USENIX FAST '15), the design paper
- https://en.wikipedia.org/wiki/F2FS — Wikipedia overview

