# Coherent FS (`Coherent`)

Mark Williams Coherent OS filesystem image — true in-place R/W via V7-style inode + zone mutation. Add scans the inode table for free slots and the data area for unreferenced zones (direct + single-indirect + double-indirect tiers, grows past s_fsize when exhausted). Replace rewrites payload bytes at the same on-disk block offsets when the new size fits the inode's existing zones. Remove zeroes data + indirect pointer blocks + dirent + inode slot. Subdirectory mutation deferred (root-level only).

> Generated from the implementation. Edit the doc comments on the descriptor,
> reader or writer rather than this file; a test regenerates it and fails on drift.

## At a glance

| | |
|---|---|
| Category | Archive |
| Family | Archive |
| Default extension | `.coh` |
| Recognised extensions | `.coh`, `.coherent` |

## Detection

| Bytes | At offset | Confidence |
|---|---|---|
| `6E 6F 6E 61 6D 65` | 484 | 0.60 |

## Verbs

| Verb | Offered | What it does |
|---|---|---|
| list / extract | yes | read the volume and copy files out of it |
| create | yes | write a fresh volume holding the given files |
| add / remove | yes | change a volume in place |
| defragment | yes | lay the volume out again |
| wipe free space | no | zero what no file holds |
| shrink | yes | reduce the volume to what it needs |
| optimise layout | no | re-lay the volume at a chosen geometry |
| report layout | no | say where every byte belongs |
| move blocks | no | relocate a run and repoint what names it |
| move metadata | no | relocate the volume's own structures |

### How it defragments

By rebuilding: every file is read out and a fresh volume is written in the
order the requested layout asks for. Correct, but it costs the whole payload.

## How a volume is laid out

### CoherentFormatDescriptor

Descriptor for Mark Williams Coherent OS file system. Coherent carries no numeric magic — it is recognised by the coh_super_block s_fname/s_fpack volume strings ("noname"/"nopack"), which is exactly how the Linux sysv driver's detect_coherent() identifies it. References:

### CoherentReader

Reader for Mark Williams Coherent OS file system (1983-1995). Coherent is a commercial UNIX V7/System V clone with a near-identical s5fs-style layout but a distinct 16-bit magic (0xFD18 at superblock+504) and 14-character directory entries like Minix v1's 14-name variant. Block size is 512 by default (sometimes 1024). Inode size is 64 bytes with 13 block pointers (10 direct + 1/2/3 indirect) stored as 3-byte addresses. Root inode = 2. Spec source: Mark Williams Company "The Coherent Operating System Reference Manual" (1992); Coherent kernel header /usr/include/sys/filsys.h.

### CoherentWriter

Builds minimal Mark Williams Coherent OS filesystem images compatible with `CoherentReader`. WORM emission: produces a fresh image; existing content is overwritten. Layout (BlockSize = 512, matches the reader's hard-coded assumptions): `block 0 boot block (zeros) block 1 padding (zeros) block 2.. inode list — 8 inodes per block, root = inode 2. The Coherent superblock structure overlaps the start of the inode list area: the magic 0xFD18 lives at file offset 1528 (= 1024 + 504), which falls into the same 512-byte block as inode 1. Inode 1 is reserved on V7- derived UNIX layouts so the overlap is benign — we never emit a real inode at index 1. block 2+isize data zones (directories then files)` The writer fills in V7-flavoured superblock fields (s_isize, s_fsize, s_nfree/s_free free-block cache, s_ninode/s_inode free-inode cache, s_time, magic 0xFD18) so an external Coherent-aware reader can mount the image (the in-tree reader only checks the magic). Files use direct zone pointers (up to 10 per inode, 5120 bytes with 512-byte blocks). Larger files use one single-indirect zone (extra 512/3 ≈ 170 zones = 87,040 bytes). Larger still falls back to the double-indirect zone slot for up to ~14.5 MB per file. The directory hierarchy is flat: every input is added under the root inode using its leaf filename (Coherent dir entries are 16 bytes total, 14 bytes for the name, so longer names are truncated).

## Storage methods

- `stored` — Stored

## Further reading

- https://github.com/torvalds/linux/tree/v6.8/fs/sysv — Linux sysv driver (incl. detect_coherent()); pinned at v6.8, the last release before its removal
- Mark Williams Company "COHERENT" manual — original vendor documentation of the filesystem
- https://en.wikipedia.org/wiki/Coherent_(operating_system) — Wikipedia overview

