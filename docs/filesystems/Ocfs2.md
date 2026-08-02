# OCFS2 (Oracle Cluster Filesystem 2) (`Ocfs2`)

OCFS2 (Oracle Cluster Filesystem 2) — spec-correct reader (INODE01 dinodes, real ocfs2_dinode offsets, 8-byte inline-data header, 16-byte extent-list header) that parses real mkfs.ocfs2 images as well as our own; extent-based writer with true in-place Add/Replace/Remove on the root directory via Ocfs2InPlaceModifier (O(touched bytes) random-access I/O). Written superblock is read by the reference debugfs.ocfs2, but the writer does not yet emit the full journal/chain-allocator system files, so written images are not yet fsck.ocfs2-clean/mountable. Subdirectory and extent-backed-root mutations fall back to the rebuild path. Single-node only — DLM/heartbeat lockdown and multi-node cluster semantics are out of scope.

> Generated from the implementation. Edit the doc comments on the descriptor,
> reader or writer rather than this file; a test regenerates it and fails on drift.

## At a glance

| | |
|---|---|
| Category | Archive |
| Family | Archive |
| Default extension | `.ocfs2` |
| Recognised extensions | `.ocfs2` |

## Detection

| Bytes | At offset | Confidence |
|---|---|---|
| `4F 43 46 53 56 32` | 8192 | 0.85 |

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

By moving what is out of place, through `Ocfs2BlockMover`.
A run is copied and whatever records its position is rewritten, so the cost is
the bytes that actually move rather than the whole volume.

| Property | Value | Meaning |
|---|---|---|
| Repoints runs independently | no | whether a file in several pieces can be moved one piece at a time |
| Relinks a whole allocation | no | whether a scattered file's chain can be restated in one call |
| Holds runs outside the volume | yes | whether a full volume can be rearranged by lifting a run into memory |

## How a volume is laid out

### Ocfs2FormatDescriptor

R/W descriptor for OCFS2 (Oracle Cluster Filesystem 2). Supports: list, extract, create, true in-place modify (Add/Replace/Remove via `Ocfs2InPlaceModifier`), defragment, extent map.

Reading is spec-correct against fs/ocfs2/ocfs2_fs.h (see `Ocfs2Reader`): INODE01 dinode signatures, the real ocfs2_dinode field offsets, the 8-byte ocfs2_inline_data header, and the 16-byte extent-list header. It reads images produced by the reference mkfs.ocfs2 as well as the toolkit's own writer (verified by an external conformance test that reads a real mkfs.ocfs2 -M local volume).

Writing produces a single-node (no DLM) image with 4 KB blocks/clusters, inline directory entries, and extent-based file data. The superblock and dinode layout are spec-correct — the reference debugfs.ocfs2 stats reads the written superblock at exit 0. The writer does NOT yet emit the full chain-allocator / journal / slot-map / local-alloc system-file suite a mountable volume needs, so fsck.ocfs2 does not pass on a written image. Create/modify are therefore scoped to structurally-correct construction with self/round-trip readback, not fsck-clean conformance.

Modifier scope: root-directory mutations only (subdirectory and extent-backed root directory paths fall back to the rebuild path). DLM/heartbeat lockdown and multi-node cluster semantics are out of scope by design.

References:

### Ocfs2Reader

Reads an OCFS2 (Oracle Cluster Filesystem 2) image and surfaces the regular files of its directory tree. Works against images produced by the reference `mkfs.ocfs2` tool as well as the toolkit's own `Ocfs2Writer`. The reader is spec-correct against `fs/ocfs2/ocfs2_fs.h` rather than matching the writer's historical (incorrect) field placement: Block size is taken from the superblock (`s_blocksize_bits`); the root directory block from `s_root_blkno`. Read-only.

### Ocfs2Writer

Builds a complete, fsck-clean OCFS2 (Oracle Cluster Filesystem 2) image from scratch in the single-node "local" (non-clustered) variant — the layout the reference `mkfs.ocfs2 -M local -N 1` produces with feature set `local | extended-slotmap | inline-data | append-dio` (incompat 0x8148) and `strict-journal-super` (compat 0x2). No metaecc, so the per-block `ocfs2_block_check` (CRC32C + ECC) stays zero. Fixed block layout (4 KB block == 4 KB cluster): `0,1 reserved 2 superblock dinode ("OCFSV2") 3 global_bitmap group descriptor (GROUP01, chain 0) 4 global_inode_alloc group descriptor (GROUP01, chain 0) 5 root directory dinode (inline dir) 6 system directory dinode (inline dir) 7 bad_blocks dinode 8 global_inode_alloc dinode (chain allocator) 9 slot_map dinode 10 heartbeat dinode 11 global_bitmap dinode (chain allocator over all clusters) 12 orphan_dir:0000 dinode (inline dir) 13 extent_alloc:0000 dinode (chain allocator, empty) 14 inode_alloc:0000 dinode (chain allocator, per-slot inodes) 15 journal:0000 dinode (JBD2) 16 local_alloc:0000 dinode 17 truncate_log:0000 dinode` The global_inode_alloc group at block 4 owns a contiguous run of blocks (blocks 4..4+groupBits-1); every system dinode is a bit within it. Heartbeat, journal and slot_map data follow; then the per-slot inode_alloc group, which owns lost+found and all user-file dinodes; finally user file data clusters. The global_bitmap marks every block up to the end of the inode_alloc group as used, mirroring mkfs.

## Parameters

| Key | Kind | Default | Allowed | Meaning |
|---|---|---|---|---|
| `VolumeLabel` | String | `` | any | Volume name shown by file managers (max 63 chars). |

## Storage methods

- `stored` — Stored

## Further reading

- https://github.com/torvalds/linux/blob/master/fs/ocfs2/ocfs2_fs.h — canonical on-disk header
- https://www.kernel.org/doc/html/latest/filesystems/ocfs2.html — kernel documentation
- https://en.wikipedia.org/wiki/OCFS2 — Wikipedia article

