# UFS (`Ufs`)

Unix File System (UFS1) image

> Generated from the implementation. Edit the doc comments on the descriptor,
> reader or writer rather than this file; a test regenerates it and fails on drift.

## At a glance

| | |
|---|---|
| Category | Archive |
| Family | Archive |
| Default extension | `.ufs` |
| Recognised extensions | `.ufs` |

## Detection

| Bytes | At offset | Confidence |
|---|---|---|
| `54 19 01 00` | 9564 | 0.90 |

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
| move blocks | yes | relocate a run and repoint what names it |
| move metadata | no | relocate the volume's own structures |

### How it defragments

By moving what is out of place, through `UfsBlockMover`.
A run is copied and whatever records its position is rewritten, so the cost is
the bytes that actually move rather than the whole volume.

| Property | Value | Meaning |
|---|---|---|
| Repoints runs independently | no | whether a file in several pieces can be moved one piece at a time |
| Relinks a whole allocation | no | whether a scattered file's chain can be restated in one call |
| Holds runs outside the volume | no | whether a full volume can be rearranged by lifting a run into memory |

## How a volume is laid out

### UfsFormatDescriptor

R/W descriptor for UFS1 (Berkeley Fast File System) images at the byte-exact `newfs -O1` layout. References:

### UfsReader

Reads UFS1 (FreeBSD/BSD FFS) filesystem images. Decodes the superblock at `SBLOCK_UFS1 = 8192`, locates CG 0's inode table, walks the root directory (inode 2) and extracts file contents via `di_db[]` direct block pointers (indirect blocks are not followed — our writer never uses them).

All field offsets mirror FreeBSD's struct fs (sys/ufs/ffs/fs.h) and struct ufs1_dinode (sys/ufs/ufs/dinode.h). fs_magic sits at the last 4 bytes of the 1376-byte superblock (offset 1372).

### UfsWriter

Writes a UFS1 (FreeBSD FFS) filesystem image that faithfully reproduces a `newfs -O1` layout: multiple cylinder groups, per-group superblock backups, a root directory plus a `.snap` directory (exactly as newfs emits), and direct-block-plus-single-indirect file extents.

All on-disk structures (superblock struct fs, cylinder-group header struct cg, and ufs1_dinode) use the exact field offsets defined in FreeBSD's sys/ufs/ffs/fs.h and sys/ufs/ufs/dinode.h. The primary superblock lives at SBLOCK_UFS1 = 8192; each cylinder group carries a backup at cgbase(cg) + fs_sblkno. Free-block / free-inode bitmaps, the fragment-summary array, cluster summaries, and the per-group cs_* summary records are populated so that fsck_ffs -f -n passes all five phases cleanly.

### UfsExtentMap

Walks a UFS1 (FreeBSD/BSD FFS) image and yields the actual on-disk byte layout — superblock + cylinder-group inode table as `MetadataReserved`, every per-file direct-block pointer run (coalesced) as a `Used` extent. Mirrors the single-CG profile `UfsReader` understands. Indirect blocks are not followed (our writer doesn't emit them).

Streaming: never loads the whole image. All reads flow through a `SectorCache` so multi-GB UFS images work without OOM.

## Parameters

| Key | Kind | Default | Allowed | Meaning |
|---|---|---|---|---|
| `VolumeLabel` | String | `` | any | Volume name shown by file managers (max 31 chars). |

## Storage methods

- `stored` — Stored

## Further reading

- McKusick, Joy, Leffler, Fabry — "A Fast File System for UNIX" (ACM TOCS, 1984), the defining FFS paper
- https://github.com/freebsd/freebsd-src/tree/main/sys/ufs — canonical implementation (ffs/fs.h on-disk superblock)
- https://en.wikipedia.org/wiki/Unix_File_System — Wikipedia article

