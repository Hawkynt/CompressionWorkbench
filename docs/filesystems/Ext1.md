# ext1 (`Ext1`)

ext1 (1992) Linux filesystem image — round-trip WORM, no Linux mkfs.

> Generated from the implementation. Edit the doc comments on the descriptor,
> reader or writer rather than this file; a test regenerates it and fails on drift.

## At a glance

| | |
|---|---|
| Category | Archive |
| Family | Archive |
| Default extension | `.ext1` |
| Recognised extensions | `.ext1` |

## Detection

| Bytes | At offset | Confidence |
|---|---|---|
| `51 EF` | 1080 | 0.90 |

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
| move blocks | yes | relocate a run and repoint what names it |
| move metadata | no | relocate the volume's own structures |

### How it defragments

By moving what is out of place, through `Ext1BlockMover`.
A run is copied and whatever records its position is rewritten, so the cost is
the bytes that actually move rather than the whole volume.

| Property | Value | Meaning |
|---|---|---|
| Repoints runs independently | no | whether a file in several pieces can be moved one piece at a time |
| Relinks a whole allocation | yes | whether a scattered file's chain can be restated in one call |
| Holds runs outside the volume | no | whether a full volume can be rearranged by lifting a run into memory |

## How a volume is laid out

### Ext1FormatDescriptor

Descriptor for ext1 filesystem images — the 1992 predecessor of ext2 by Rémy Card. ext1's on-disk superblock layout is identical to the GOOD_OLD-revision ext2 superblock with one crucial difference: the s_magic field at offset 56 of the superblock (file-relative offset 1080) reads `0xEF51` instead of ext2's `0xEF53`. ext1 has no journal, no extents, and no FEATURE_INCOMPAT_FILETYPE — directory entries are 8-byte fixed-header (with a 16-bit `name_len`) + name only. Detection, structural surfacing and round-trip read+write of small WORM images are supported; vintage pre-1993 Linux disk images and forensic tooling for early Linux installs are the consumers. References:

### Ext1Reader

Reads ext1 (1992) filesystem images — the predecessor of ext2 by Rémy Card. Identical to GOOD_OLD-revision ext2 byte-for-byte except:

Only direct + indirect block pointers are honoured (no extents, since extents arrived with ext4). Use `Ext1Reader` for full file content extraction; the broader `Ext1FormatDescriptor` still surfaces a FULL.ext1 + metadata view.

### Ext1Writer

Builds minimal ext1 (1992) filesystem images from scratch — the predecessor of ext2 by Rémy Card. The on-disk superblock layout is identical to GOOD_OLD-revision ext2 byte-for-byte except for the magic value (`0xEF51` instead of ext2's `0xEF53`) at offset 1080 of the file.

Differences from the ext2 writer:

No mkfs.ext1 exists — ext1's magic was retired in 1993, so no Linux validator can mount or fsck the resulting images. Tests verify our reader can round-trip the output.

### Ext1ExtentMap

Walks an ext1 image and yields its actual on-disk byte layout — per-file block-pointer runs plus metadata regions (superblock, BGD table, block + inode bitmaps, inode table). ext1 is rev-0 only: 128-byte inodes, no extents, 8-byte directory header with 16-bit name_len. Used by the defragment window's block-map preview.

Streaming: never loads the whole image. All reads flow through a `SectorCache` so multi-GB ext1 images work without OOM.

## Parameters

| Key | Kind | Default | Allowed | Meaning |
|---|---|---|---|---|
| `BlockSize` | Integer | `1024` | `1024`, `2048`, `4096` | ext1 block size (s_log_block_size). The 4 MiB image footprint is constant; larger blocks mean fewer total blocks. |

## Storage methods

- `stored` — Stored

## Further reading

- https://e2fsprogs.sourceforge.net/ext2intro.html — Card/Ts'o/Tweedie, "Design and Implementation of the Second Extended Filesystem", which documents the original ext it replaced
- https://mirrors.edge.kernel.org/pub/linux/kernel/Historic/ — historic kernel trees whose fs/ext is the primary source for the 1992 layout
- https://en.wikipedia.org/wiki/Extended_file_system — Wikipedia article on the original ext

