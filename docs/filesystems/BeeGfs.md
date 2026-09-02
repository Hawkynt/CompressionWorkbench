# BeeGFS (`BeeGfs`)

BeeGFS — Stage 0 detection only. Distributed parallel cluster FS (Fraunhofer, ex-FhGFS): the namespace lives across metadata-target processes (per-inode files + xattrs on a regular Linux FS like ext4/xfs), file payload lives across storage-target processes (chunk files in a hashed dir layout on a regular Linux FS). No standalone on-disk image — a volume cannot be represented as a single byte-stream. R/O promotion would require traversing a live metadata target directory tree + resolving the stripe pattern + target group map via beegfs-meta. Magic 'BeeGFS' / 0x42656547 at offset 0 of a chunk-file or dump-tool output is the only single-stream surface available.

> Generated from the implementation. Edit the doc comments on the descriptor,
> reader or writer rather than this file; a test regenerates it and fails on drift.

## At a glance

| | |
|---|---|
| Category | Archive |
| Family | Archive |
| Default extension | `.beegfs` |
| Recognised extensions | `.beegfs` |

## Detection

| Bytes | At offset | Confidence |
|---|---|---|
| `42 65 65 47 46 53` | 0 | 0.90 |
| `42 65 65 47` | 0 | 0.85 |

## Verbs

| Verb | Offered | What it does |
|---|---|---|
| list / extract | yes | read the volume and copy files out of it |
| create | no | write a fresh volume holding the given files |
| add / remove | no | change a volume in place |
| defragment | no | lay the volume out again |
| wipe free space | no | zero what no file holds |
| shrink | no | reduce the volume to what it needs |
| optimise layout | no | re-lay the volume at a chosen geometry |
| report layout | no | say where every byte belongs |
| move blocks | no | relocate a run and repoint what names it |
| move metadata | no | relocate the volume's own structures |

### How it defragments

It does not.

## How a volume is laid out

### BeeGfsFormatDescriptor

Stage 0 detection-only descriptor for BeeGFS chunk-file / dump tags. Surfaces only a synthetic `metadata.ini` and the raw image bytes; no real file-walk is attempted because a BeeGFS volume has no standalone on-disk image. References:

### BeeGfsReader

Stage 0 detection-only reader for BeeGFS chunk-file / dump tags. BeeGFS (Fraunhofer Parallel Cluster FS, originally FhGFS) is a distributed parallel cluster filesystem. There is no standalone on-disk image format for a BeeGFS volume: the namespace lives across one or more metadata targets (each a directory tree on a regular Linux FS like ext4/xfs, with per-inode metadata stored as files + extended attributes), and the file payload lives across many storage targets (chunk files in a 2-level hash directory layout on the storage targets' regular Linux FS). Reconstructing a single logical file requires the live metadata-server stripe pattern + storage-target map; a single byte-stream cannot represent it. This descriptor therefore only verifies the ASCII tag `"BeeGFS"` (6 bytes, 0x42 0x65 0x65 0x47 0x46 0x53) or the short 4-byte tag `"BeeG"` (0x42 0x65 0x65 0x47 = 0x42656547 BE) at offset 0 of a chunk-file or dump produced by a BeeGFS utility, and surfaces a synthetic `metadata.ini` documenting the tag + a raw `beegfs-chunk.bin` blob containing the file bytes verbatim. Promotion to R/O is not possible from a single stream — see `Description` on the descriptor.

## Storage methods

- `stored` — Stored

## Further reading

- https://www.beegfs.io — official BeeGFS site and documentation portal
- https://github.com/ThinkParQ/beegfs — BeeGFS source (ThinkParQ)
- https://en.wikipedia.org/wiki/BeeGFS — Wikipedia overview

