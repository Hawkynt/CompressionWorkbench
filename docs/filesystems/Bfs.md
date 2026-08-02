# BFS (`Bfs`)

BeOS / Haiku BFS filesystem image

> Generated from the implementation. Edit the doc comments on the descriptor,
> reader or writer rather than this file; a test regenerates it and fails on drift.

## At a glance

| | |
|---|---|
| Category | Archive |
| Family | Archive |
| Default extension | `.bfs` |
| Recognised extensions | `.bfs`, `.img` |

## Detection

| Bytes | At offset | Confidence |
|---|---|---|
| `31 53 46 42` | 544 | 0.35 |
| `31 53 46 42` | 32 | 0.30 |

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

By moving what is out of place, through `BfsBlockMover`.
A run is copied and whatever records its position is rewritten, so the cost is
the bytes that actually move rather than the whole volume.

| Property | Value | Meaning |
|---|---|---|
| Repoints runs independently | yes | whether a file in several pieces can be moved one piece at a time |
| Relinks a whole allocation | no | whether a scattered file's chain can be restated in one call |
| Holds runs outside the volume | yes | whether a full volume can be rearranged by lifting a run into memory |

## How a volume is laid out

### BfsFormatDescriptor

R/W descriptor for BeOS / Haiku BFS filesystem images. Can list, extract, create (WORM), modify (via rebuild), and defragment BFS images. The writer produces a minimal single-AG image with a single B+ tree leaf for the root directory and direct block_run extents for file data. References:

### BfsReader

Reads files from a BFS filesystem image. Parses the superblock, walks each directory's B+ tree leaf chain (following right_link across sibling leaves), and extracts file data from direct block_run extents. Supports directories whose entries span multiple chained leaves; does not traverse interior/index nodes or indirect/double-indirect extents.

### BfsWriter

Builds a minimal BFS (BeOS / Haiku) filesystem image from scratch. Produces a 4 MB image with 1024-byte blocks, 1 allocation group, a chain of B+ tree leaves per directory (entries spill across sibling leaves linked by left_link/right_link when they exceed one node), and direct block_run extents for file data (no indirect/double-indirect).

On-disk layout:

Each entry costs ~10 + name_length bytes in a leaf; a 1024-byte leaf holds roughly 40–60 short-named files. Directories larger than that spill across additional leaf blocks chained via right_link, so a single directory can hold thousands of entries (bounded only by the image size).

## Storage methods

- `stored` — Stored

## Further reading

- "Practical File System Design with the Be File System" (Dominic Giampaolo, Morgan Kaufmann, 1999) — the canonical BFS on-disk reference by its author
- https://github.com/haiku/haiku/tree/master/src/add-ons/kernel/file_systems/bfs — Haiku's maintained BFS implementation
- https://en.wikipedia.org/wiki/Be_File_System — Wikipedia overview

