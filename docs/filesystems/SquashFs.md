# SquashFS (`SquashFs`)

Linux compressed read-only filesystem

> Generated from the implementation. Edit the doc comments on the descriptor,
> reader or writer rather than this file; a test regenerates it and fails on drift.

## At a glance

| | |
|---|---|
| Category | Archive |
| Family | Archive |
| Default extension | `.sqfs` |
| Recognised extensions | `.sqfs`, `.squashfs`, `.snap`, `.appimage` |

## Detection

| Bytes | At offset | Confidence |
|---|---|---|
| `68 73 71 73` | 0 | 0.95 |
| `73 71 73 68` | 0 | 0.95 |

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

### SquashFsFormatDescriptor

R/W descriptor for SquashFS images ("hsqs" magic) — the compressed read-only filesystem used by live media and embedded Linux; this writer emits gzip-compressed images. References:

### SquashFsReader

Reads a SquashFS version 4 filesystem image.

### SquashFsWriter

Writes a SquashFS version 4 filesystem image using gzip (zlib) compression for data blocks. Metadata blocks (inodes, directories, IDs) use zlib compression with automatic fallback to uncompressed when compression does not reduce size.

## Parameters

| Key | Kind | Default | Allowed | Meaning |
|---|---|---|---|---|
| `BlockSize` | Enum | `128 KB` | `Auto`, `4 KB`, `8 KB`, `16 KB`, `32 KB`, `64 KB`, `128 KB`, `256 KB`, `512 KB`, `1 MB` | Compressed data block size. SquashFS allows powers of two from 4 KB to 1 MB; larger blocks compress better but waste more on small files. |

## Storage methods

- `squashfs` — SquashFS

## Further reading

- https://dr-emann.github.io/squashfs/ — community-written binary-format specification
- https://www.kernel.org/doc/html/latest/filesystems/squashfs.html — kernel documentation
- https://github.com/plougher/squashfs-tools — canonical mksquashfs/unsquashfs tooling
- https://en.wikipedia.org/wiki/SquashFS — Wikipedia article

