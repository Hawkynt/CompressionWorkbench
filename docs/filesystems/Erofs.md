# EROFS (`Erofs`)

Android read-only compressed filesystem; uncompressed + inline inode layouts.

> Generated from the implementation. Edit the doc comments on the descriptor,
> reader or writer rather than this file; a test regenerates it and fails on drift.

## At a glance

| | |
|---|---|
| Category | Archive |
| Family | Archive |
| Default extension | `.erofs` |
| Recognised extensions | `.erofs`, `.img` |

## Detection

| Bytes | At offset | Confidence |
|---|---|---|
| `E2 E1 F5 E0` | 1024 | 0.95 |

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

By moving what is out of place, through `ErofsBlockMover`.
A run is copied and whatever records its position is rewritten, so the cost is
the bytes that actually move rather than the whole volume.

| Property | Value | Meaning |
|---|---|---|
| Repoints runs independently | no | whether a file in several pieces can be moved one piece at a time |
| Relinks a whole allocation | no | whether a scattered file's chain can be restated in one call |
| Holds runs outside the volume | yes | whether a full volume can be rearranged by lifting a run into memory |

## How a volume is laid out

### ErofsFormatDescriptor

Descriptor for EROFS images. Reading covers the uncompressed + inline inode layouts; creation produces a minimal uncompressed (FLAT_PLAIN) image via `ErofsWriter`. Full-fidelity, compressed images remain the job of `mkfs.erofs`; our writer targets the round-trippable WORM subset (compact inodes, plain data, nested directories). References:

### ErofsReader

Reads EROFS (Enhanced Read-Only File System) images as used by Android system/APEX partitions and produced by `mkfs.erofs`. Handles the uncompressed inode layouts (FLAT_PLAIN and FLAT_INLINE) for both compact (32-byte) and extended (64-byte) inodes; LZ4 / LZMA compressed clusters and fragments are deferred — an inode with a compressed datalayout surfaces as a zero-length / unsupported payload rather than failing the whole image.

The superblock lives at file offset 1024; on-disk magic is the little-endian word 0xE0F5E1E2 (bytes E2 E1 F5 E0). Block size is 2^sb.blkszbits (almost always 4096). A node id (nid) addresses a 32-byte granule measured from meta_blkaddr * blockSize, i.e. the inode lives at meta_blkaddr * blockSize + nid * 32.

### ErofsWriter

Builds a valid, uncompressed EROFS image from a set of files and their (possibly nested) paths, matching the on-disk encoding produced by `mkfs.erofs` for plain data and accepted by `fsck.erofs`: Directories are emitted as EROFS directory chunks: a contiguous array of 12-byte `erofs_dirent` headers followed by the packed entry names, with the conventional "." and ".." entries first. Directory bodies follow the same FLAT_INLINE rule.

## Parameters

| Key | Kind | Default | Allowed | Meaning |
|---|---|---|---|---|
| `VolumeLabel` | String | `` | any | Volume name shown by file managers (max 16 chars). |

## Storage methods

- `stored` — Stored

## Further reading

- https://docs.kernel.org/filesystems/erofs.html — Linux kernel EROFS documentation (on-disk overview)
- https://github.com/torvalds/linux/tree/master/fs/erofs — mainline implementation (erofs_fs.h defines the on-disk structures)
- https://en.wikipedia.org/wiki/EROFS — Wikipedia overview

