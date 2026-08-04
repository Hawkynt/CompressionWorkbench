# HPFS (`Hpfs`)

OS/2 High Performance File System — read/write with direct-allocation layout.

> Generated from the implementation. Edit the doc comments on the descriptor,
> reader or writer rather than this file; a test regenerates it and fails on drift.

## At a glance

| | |
|---|---|
| Category | Archive |
| Family | Archive |
| Default extension | `.img` |
| Recognised extensions | `.img`, `.hpfs` |

## Detection

| Bytes | At offset | Confidence |
|---|---|---|
| `49 E8 95 F9` | 8192 | 0.85 |

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

By moving what is out of place, through `HpfsBlockMover`.
A run is copied and whatever records its position is rewritten, so the cost is
the bytes that actually move rather than the whole volume.

| Property | Value | Meaning |
|---|---|---|
| Repoints runs independently | no | whether a file in several pieces can be moved one piece at a time |
| Relinks a whole allocation | no | whether a scattered file's chain can be restated in one call |
| Holds runs outside the volume | yes | whether a full volume can be rearranged by lifting a run into memory |

## How a volume is laid out

### HpfsFormatDescriptor

R/W descriptor for OS/2 HPFS (High Performance File System) volumes. Supports: list, extract, create, modify (true in-place at root level via `HpfsInPlaceModifier`), defragment, extent map. References:

Add/Remove at the root level mutate the image in place — bitmap bits at LBA 24 are flipped, fresh data + FNODE sectors are written into previously-free slots, and root-DIRBLK dirents are shifted in-place at LBA 20. Sectors not touched by the mutation stay byte-identical to their pre-mutation bytes. Subdirectory mutation and multi-block DIRBLK B-tree splits are deferred and throw `NotSupportedException` / `InvalidOperationException` respectively; callers can fall back to a rebuild path in those cases.

### HpfsReader

Read-only reader for OS/2 HPFS (High Performance File System) volumes.

Scope (intentionally narrow — enough for typical test images):

Larger files (those whose fnode height field is non-zero, indicating an AllocSec B-tree) are listed but return empty byte arrays on extract; this is documented as deferred.

Layout references:

### HpfsWriter

Builds a minimal HPFS (OS/2 High Performance File System) image from scratch. Layout: LBA 0: Boot sector (BPB + OEM ID) LBA 16: Superblock (8-byte magic + root fnode LBA + total sectors + bitmap start) LBA 17: Spare block (8-byte magic, minimal) LBA 18: Root fnode (magic + direct alloc pointing to root dir block) LBA 20..23: Root directory block (2048 bytes = 4 LBAs, with dir entries) LBA 24: Bitmap band 0 (allocation bitmap for the whole volume) LBA 32+: Per-directory fnodes + dir blocks, then file fnodes and data. Directories are honoured: a name passed to `AddFile` may contain '/' (or '\') separators; each path segment becomes a real HPFS directory (an fnode with the directory flag, referenced by a directory-flagged dirent in the parent's dirent block, with its own dirent block). A directory whose children overflow one 2 KiB dirent block spills into additional leaf dirent blocks organised as a 2-level dirent B-tree: the directory's root block holds separator dirents whose down-pointers reference the leaf blocks. With short names this scales to well over a thousand entries per directory (the 2-level root block holds roughly 40 separators, i.e. ~40 leaves of ~45 entries each). Other limitations remain: direct file allocation only (no AllocSec B-tree), and a single bitmap band.

### HpfsLayout

Where the fields of an HPFS fnode actually are.

These follow struct fnode in the kernel's HPFS driver. The offsets used here before were not that struct: the parent pointer sat in the middle of the short name, the directory flag in the ACL length, and the allocation list 138 bytes past where it belongs — inside the user-id field. A volume written that way was self-consistent and read by nothing else.

The allocation header is also checked, not merely read. A driver insists that the used and free node counts add up to the number of slots that follow — eight runs, or twelve subtree pointers — and rejects the fnode when they do not, which is what "bad number of nodes in fnode" means.

## Storage methods

- `stored` — Stored

## Further reading

- Ray Duncan, "Design Goals and Implementation of the New High Performance File System" (Microsoft Systems Journal, September 1989) — the original published description
- https://docs.kernel.org/filesystems/hpfs.html — Linux kernel HPFS driver documentation; fs/hpfs is the maintained on-disk reference
- https://en.wikipedia.org/wiki/High_Performance_File_System — Wikipedia overview

