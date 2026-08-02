# JFS (`Jfs`)

IBM Journaled File System image (R/W: arbitrary-depth dtree mutation w/ long names + recursive subdir removal)

> Generated from the implementation. Edit the doc comments on the descriptor,
> reader or writer rather than this file; a test regenerates it and fails on drift.

## At a glance

| | |
|---|---|
| Category | Archive |
| Family | Archive |
| Default extension | `.jfs` |
| Recognised extensions | `.jfs` |

## Detection

| Bytes | At offset | Confidence |
|---|---|---|
| `4A 46 53 31` | 32768 | 0.90 |

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

By moving what is out of place, through `JfsBlockMover`.
A run is copied and whatever records its position is rewritten, so the cost is
the bytes that actually move rather than the whole volume.

| Property | Value | Meaning |
|---|---|---|
| Repoints runs independently | yes | whether a file in several pieces can be moved one piece at a time |
| Relinks a whole allocation | no | whether a scattered file's chain can be restated in one call |
| Holds runs outside the volume | yes | whether a full volume can be rearranged by lifting a run into memory |

## How a volume is laid out

### JfsFormatDescriptor

Descriptor for IBM JFS1 aggregate images. Reader walks the kernel-fixed AIT (block 11), the indirect fileset AIM → IAG → FSIT path, and the inline dtree root + xtree extents. Writer emits a complete WORM image with FILESYSTEM_I → AIM → IAG → FSIT, dual superblocks, dmap+dmapctl with canonical `ujfs_adjtree` buddy tree, both AIT/AIM copies, and an inline-dtroot root directory with up to 8 user files. Validated clean against real `fsck.jfs -n -f -v`.

State: R/W (extended mutation past leaf-only). `JfsMutator` implements: Operations that genuinely need multi-week scope still throw `NotSupportedException` with a SPECIFIC message naming what's unsupported: inline dtroot leaf split, external dtree leaf split, xtree root promotion to non-leaf, IAG full / FSIT extent growth.

References:

### JfsReader

Reads IBM JFS1 aggregate images produced by `JfsWriter` or by real `mkfs.jfs`. Decodes the superblock, FILESYSTEM_I aggregate inode (#16), fileset inode table, and the inline dtree root directory (UCS-2 names).

### JfsWriter

Writes a minimal IBM Journaled File System (JFS1) aggregate image with a single allocation group, one fileset, and an inline dtree root directory.

Byte layout matches the on-disk structures in linux/fs/jfs and the jfsutils reference (mkfs.jfs / fsck.jfs); validated by exit-zero from fsck.jfs -n -f -v. All integer fields are little-endian. pxd_t is packed as len_addr = (len & 0xFFFFFF) | ((addr >> 32) << 24), addr2 = addr & 0xFFFFFFFF. Dtree slot names are UCS-2 (UTF-16 LE). Round-trips through `JfsReader`.

Aggregate inode table (block 11..14, IXSIZE=16 KB) holds the AGGR_RESERVED_I (0), AGGREGATE_I (1, → AIM), BMAP_I (2, → block-allocation map), LOG_I (3), BADBLOCK_I (4) and FILESYSTEM_I (16, → fileset AIM) metadata inodes. The fileset inode table at blocks 29..32 holds FILESET_RSVD_I (0), FILESET_EXT_I (1), ROOT_I (2, dtroot inline), ACL_I (3) and user file inodes (4+).

### JfsExtentMap

Reads a JFS volume's layout: which blocks are in use, and for the blocks a file's own xtree names, which file they belong to.

JFS keeps one dmap page per 8192 blocks; the page's persistent bitmap holds one bit per block, set when the block is allocated. What the bitmap leaves clear is exactly the free space — including the blocks a removed file used to occupy, which still hold its bytes. The map is contiguous from `FirstDmapBlock`, and each page states which range it covers in its own header, so the walk validates that against where it expected the page to be rather than assuming the layout.

The bitmap alone says which blocks are taken and nothing about by whom, which is enough to wipe a volume and not enough to lay one out again: a run with no owner has nothing to repoint. So each file's extents are read from its xtree and reported under its name, and what the bitmap claims beyond them is the volume's own structures.

## Parameters

| Key | Kind | Default | Allowed | Meaning |
|---|---|---|---|---|
| `VolumeLabel` | String | `` | any | JFS volume label stored in s_label (max 16 ASCII chars). |

## Storage methods

- `stored` — Stored

## Further reading

- arbitrary path depth add/remove (descend by name through any intermediate directory whose dtree is inline OR external/router);
- long names via continuation slots chained through the head ldtentry's next byte (both insert and remove walk the chain);
- external dtree leaf-page insert/delete when the directory's dtroot has been promoted to a router (in-place stbl shift + freelist restore, with no split);
- recursive subdirectory removal — DFS the dtree, free every child file's xtree extents + inode + dmap bits, free the dtree pages themselves, then close out the entry in the parent;
- multi-dmap allocation — walks both dmap pages the writer reserves before declaring the image full;
- xtree extent allocate/free with inline xad slots and dmap binary-buddy ujfs_adjtree rerun for every modified dmap.
- https://jfs.sourceforge.net/project/pub/jfslayout.pdf — "JFS Layout", the official on-disk format document (superblock, dmap/dmapctl, dtree/xtree, IAG)
- https://github.com/torvalds/linux/tree/master/fs/jfs — mainline kernel implementation; jfsutils' fsck.jfs is the conformance gate
- https://en.wikipedia.org/wiki/JFS_(file_system) — Wikipedia overview

