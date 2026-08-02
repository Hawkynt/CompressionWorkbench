# JFFS2 (`Jffs2`)

Journaling Flash File System v2 — log-structured flash filesystem.

> Generated from the implementation. Edit the doc comments on the descriptor,
> reader or writer rather than this file; a test regenerates it and fails on drift.

## At a glance

| | |
|---|---|
| Category | Archive |
| Family | Archive |
| Default extension | `.jffs2` |
| Recognised extensions | `.jffs2`, `.jffs`, `.img` |

## Detection

| Bytes | At offset | Confidence |
|---|---|---|
| `85 19` | 0 | 0.35 |

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

By moving what is out of place, through `Jffs2BlockMover`.
A run is copied and whatever records its position is rewritten, so the cost is
the bytes that actually move rather than the whole volume.

| Property | Value | Meaning |
|---|---|---|
| Repoints runs independently | yes | whether a file in several pieces can be moved one piece at a time |
| Relinks a whole allocation | no | whether a scattered file's chain can be restated in one call |
| Holds runs outside the volume | yes | whether a full volume can be rearranged by lifting a run into memory |

## How a volume is laid out

### Jffs2FormatDescriptor

JFFS2 (Journaling Flash File System v2) format descriptor. Supports: list, extract, create, true in-place R/W modify (log-append per the JFFS2 spec — fresh node at the tail with bumped version, existing nodes left byte-identical), defragment, extent map. References:

### Jffs2FileReader

Reads a JFFS2 image and extracts actual file contents by reassembling inode data nodes and matching dirent nodes. Handles the common case of uncompressed, non-fragmented single-version files. Nested entries are reassembled to their full path by walking each dirent's parent-inode (pino) chain back to the root directory.

### Jffs2Writer

Builds a JFFS2 (Journaling Flash File System v2) image from scratch. Produces a valid log-structured image with cleanmarkers, inode nodes, and dirent nodes. Data is stored uncompressed (compr=0x00 NONE). Default erase block size: 128 KiB (common NOR flash). Files whose name contains path separators ('/' or '\') are placed inside a real directory tree: each intermediate path segment becomes its own directory inode plus a dirent in its parent, so nested paths round-trip through the reader instead of being flattened into the root.

## Parameters

| Key | Kind | Default | Allowed | Meaning |
|---|---|---|---|---|
| `EraseBlockSize` | Enum | `128 KB` | `Auto`, `4 KB`, `8 KB`, `16 KB`, `32 KB`, `64 KB`, `128 KB`, `256 KB`, `512 KB`, `1 MB` | Flash erase-block size. The image is padded to a whole multiple of it; common NOR flash uses 128 KB. |

## Storage methods

- `stored` — Stored

## Further reading

- https://sourceware.org/jffs2/ — original JFFS2 site (David Woodhouse), incl. the design paper
- http://www.linux-mtd.infradead.org/doc/jffs2.html — Linux MTD project's JFFS2 documentation
- https://github.com/torvalds/linux/tree/master/fs/jffs2 — mainline implementation (jffs2_fs_i.h / node headers)
- https://en.wikipedia.org/wiki/JFFS2 — Wikipedia overview

