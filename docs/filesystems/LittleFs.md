# LittleFS (`LittleFs`)

LittleFS embedded-flash FS — metadata-pair walk + CTZ/inline file extraction.

> Generated from the implementation. Edit the doc comments on the descriptor,
> reader or writer rather than this file; a test regenerates it and fails on drift.

## At a glance

| | |
|---|---|
| Category | Archive |
| Family | Archive |
| Default extension | `.littlefs` |
| Recognised extensions | `.littlefs`, `.lfs` |

## Detection

| Bytes | At offset | Confidence |
|---|---|---|
| `6C 69 74 74 6C 65 66 73` | 8 | 0.60 |
| `6C 69 74 74 6C 65 66 73` | 16 | 0.60 |

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

By rebuilding: every file is read out and a fresh volume is written in the
order the requested layout asks for. Correct, but it costs the whole payload.

## How a volume is laid out

### LittleFsFormatDescriptor

Read-only descriptor for LittleFS images (Arduino / RTOS / IoT embedded-flash FS). Surfaces the superblock and parsed geometry. Walking the tag-based metadata pair commit log with CRC validation is intentionally out of scope — that's a full reference-implementation port. Detection + structural surfacing is the win. References:

### LittleFsReader

Reads a littlefs v2 image: walks each directory's metadata-pair commit log (validating the commit CRC), follows hard-tail and directory-struct links, and resolves file structs (inline payloads and CTZ skip-lists) into byte content.

This is a focused decoder for the subset emitted by `LittleFsWriter` — a single-commit metadata pair per directory, inline structs for small files, CTZ skip-lists for the rest. It validates structure against the on-disk format (revision, delta-encoded tags, commit CRC) rather than assuming fixed offsets.

### LittleFsWriter

From-scratch (write-once) builder for a minimal but specification-accurate littlefs v2 image. Produces a root metadata pair carrying the superblock and the root directory entries, one metadata pair per subdirectory (linked via hard-tail tags), inline structs for small files, and CTZ skip-lists for files that do not fit inline. The result round-trips through `LittleFsReader`.

Layout strategy: blocks 0 and 1 form the root metadata pair (both blocks carry the same commit so either half validates). Subdirectory metadata pairs and file data blocks are allocated from a monotonically increasing block cursor. The image is sized to hold every allocated block exactly; there is no wear-levelling reserve because the image is immutable.

## Parameters

| Key | Kind | Default | Allowed | Meaning |
|---|---|---|---|---|
| `BlockSize` | Enum | `4 KB` | `Auto`, `128 B`, `256 B`, `512 B`, `1 KB`, `2 KB`, `4 KB`, `8 KB`, `16 KB`, `32 KB`, `64 KB` | Erase-block size recorded in the superblock. LittleFS allows powers of two from 128 B to 64 KB. |

## Storage methods

- `stored` — Stored

## Further reading

- https://github.com/littlefs-project/littlefs — canonical littlefs source (ARM Mbed lineage)
- https://github.com/littlefs-project/littlefs/blob/master/SPEC.md — on-disk format specification
- https://github.com/littlefs-project/littlefs/blob/master/DESIGN.md — design document (metadata pairs, CTZ skip-lists)

