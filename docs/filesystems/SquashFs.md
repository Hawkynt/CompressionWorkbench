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

By moving what is out of place, through `SquashFsBlockMover`.
A run is copied and whatever records its position is rewritten, so the cost is
the bytes that actually move rather than the whole volume.

| Property | Value | Meaning |
|---|---|---|
| Repoints runs independently | yes | whether a file in several pieces can be moved one piece at a time |
| Relinks a whole allocation | no | whether a scattered file's chain can be restated in one call |
| Holds runs outside the volume | yes | whether a full volume can be rearranged by lifting a run into memory |

## How a volume is laid out

### SquashFsFormatDescriptor

R/W descriptor for SquashFS images ("hsqs" magic) — the compressed read-only filesystem used by live media and embedded Linux; this writer emits gzip-compressed images. References:

### SquashFsReader

Reads a SquashFS version 4 filesystem image.

### SquashFsWriter

Writes a SquashFS version 4 filesystem image using gzip (zlib) compression for data blocks. Metadata blocks (inodes, directories, IDs) use zlib compression with automatic fallback to uncompressed when compression does not reduce size.

### SquashFsLayout

Reads the inode table out of its metadata blocks and finds the field in each regular file's inode that says where its data starts.

The table is a run of metadata blocks, each a two-byte length followed by that many bytes — deflated unless the top bit of the length says otherwise. So a field inside it cannot simply be written to: the block has to be taken apart, changed, and put together again.

Which is only expressible if the result still fits. A block's length is its own header, and every table after it is found by an offset in the superblock, so a block that grew would move all of them. One that shrinks is padded back to the length it had, which a deflate stream tolerates because it ends where its own final block ends.

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

