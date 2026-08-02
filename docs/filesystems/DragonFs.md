# DragonFS (`DragonFs`)

DragonFS embedded read-only filesystem (Libdragon / Nintendo 64).

> Generated from the implementation. Edit the doc comments on the descriptor,
> reader or writer rather than this file; a test regenerates it and fails on drift.

## At a glance

| | |
|---|---|
| Category | Archive |
| Family | Archive |
| Default extension | `.dfs` |
| Recognised extensions | `.dfs` |

## Detection

| Bytes | At offset | Confidence |
|---|---|---|
| `44 72 61 67 6F 6E 46 53` | 0 | 0.90 |

## Verbs

| Verb | Offered | What it does |
|---|---|---|
| list / extract | yes | read the volume and copy files out of it |
| create | yes | write a fresh volume holding the given files |
| add / remove | yes | change a volume in place |
| defragment | yes | lay the volume out again |
| wipe free space | yes | zero what no file holds |
| shrink | yes | reduce the volume to what it needs |
| optimise layout | no | re-lay the volume at a chosen geometry |
| report layout | yes | say where every byte belongs |
| move blocks | no | relocate a run and repoint what names it |
| move metadata | no | relocate the volume's own structures |

### How it defragments

By moving what is out of place, through `DragonFsBlockMover`.
A run is copied and whatever records its position is rewritten, so the cost is
the bytes that actually move rather than the whole volume.

| Property | Value | Meaning |
|---|---|---|
| Repoints runs independently | yes | whether a file in several pieces can be moved one piece at a time |
| Relinks a whole allocation | no | whether a scattered file's chain can be restated in one call |
| Holds runs outside the volume | yes | whether a full volume can be rearranged by lifting a run into memory |

## How a volume is laid out

### DragonFsFormatDescriptor

Read-only descriptor for DragonFS — the embedded read-only filesystem used by Libdragon (open Nintendo 64 SDK) to bundle assets inside an N64 ROM image. DragonFS is big-endian throughout, uses 32-byte directory records starting at file offset 256 (Libdragon DFS_ROOT_OFFSET), and lacks an unambiguous fixed magic in original images — detection is by .dfs extension plus an optional "DragonFS" ASCII tag at offset 0 for self-produced research images. References:

### DragonFsReader

Reads DragonFS images — the read-only embedded filesystem used by Libdragon (the open Nintendo 64 SDK) to bundle assets into a N64 ROM. DragonFS is big-endian throughout (MIPS R4300i convention), uses 32-byte directory records, and a singly-linked list for file chunks. Root directory entry sits at file offset 256 (Libdragon DFS_ROOT_OFFSET). Directory entry layout (32 bytes BE): 0x00 u32 next_entry_offset (0 = end of dir) 0x04 u32 flags 0x0001 = directory 0x0002 = end-of-directory marker 0x08 char[20] name (NUL-terminated, ASCII) 0x1C u32 file_size (for files) / first_entry_offset (for dirs) File data starts at offset_of_entry + 32 unless the file uses indirection (large files chain via "next chunk" pointers); this reader handles the common direct-contiguous-data case.

### DragonFsWriter

Builds a fresh, read-only DragonFS image (Libdragon / Nintendo 64) from a flat set of input files. The produced image round-trips through `DragonFsReader`. Layout produced (big-endian throughout): 0x000..0x007 "DragonFS" ASCII tag (enables self-detection) 0x008..0x107 zero padding 0x108 start of the root directory's child chain (DFS_ROOT_OFFSET = 8 + 256 = 264) Each child is a 32-byte directory record immediately followed by that file's raw bytes: 0x00 u32 next_entry_offset (absolute byte offset of the next record; 0 = last) 0x04 u32 flags (0 = regular file) 0x08 char[20] name (NUL-terminated ASCII; DragonDOS-style 8.3 leaf names) 0x1C u32 file_size File data follows the record at record_offset + 32; the next record begins at record_offset + 32 + file_size (no inter-file padding is required by the reader, but each record's start is what the previous record's next_entry_offset points at).

### DragonFsExtentMap

Reports where a DragonFS volume's bytes are: each file as its directory record followed by its data, and whatever nothing links to as free.

A file here has no address of its own. Its bytes begin immediately after the thirty-two byte record that names it, and the record is reached by a pointer in the record before it — or, for the first of a directory, by the pointer in the parent. So the unit that can be moved is the pair, and what has to be rewritten afterwards is whoever pointed at it.

## Storage methods

- `stored` — Stored

## Further reading

- https://github.com/DragonMinded/libdragon — Libdragon source, the origin of DragonFS (dragonfs.c / mkdfs define the format)
- https://libdragon.dev — official Libdragon documentation site

