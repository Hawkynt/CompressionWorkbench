# Xenix FS (`Xenix`)

Microsoft/SCO Xenix filesystem image — read + WORM emit + in-place Add/Remove via s_free/s_inode cache (Xenix V variant).

> Generated from the implementation. Edit the doc comments on the descriptor,
> reader or writer rather than this file; a test regenerates it and fails on drift.

## At a glance

| | |
|---|---|
| Category | Archive |
| Family | Archive |
| Default extension | `.xnx` |
| Recognised extensions | `.xnx`, `.xenix` |

## Detection

| Bytes | At offset | Confidence |
|---|---|---|
| `44 55 2B 00` | 2040 | 0.70 |

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

By moving what is out of place, through `XenixBlockMover`.
A run is copied and whatever records its position is rewritten, so the cost is
the bytes that actually move rather than the whole volume.

| Property | Value | Meaning |
|---|---|---|
| Repoints runs independently | yes | whether a file in several pieces can be moved one piece at a time |
| Relinks a whole allocation | no | whether a scattered file's chain can be restated in one call |
| Holds runs outside the volume | yes | whether a full volume can be rearranged by lifting a run into memory |

## How a volume is laid out

### XenixFormatDescriptor

Descriptor for Microsoft/SCO Xenix System V filesystem images. Carries the genuine Xenix superblock magic 0x2B5544 at s_magic (struct offset 0x3F8 → file offset 2040), the value the Linux sysv driver matches. Reads existing Xenix images and emits fresh WORM images via `XenixWriter`. References:

### XenixReader

Reader for Microsoft / SCO Xenix System V file system (1980-1989, Microsoft's licensed UNIX). Xenix is a System V variant with two superblock structures ("Xenix-4 V" and "Xenix-5 V"); we target the more common Xenix V (3/V) layout. On-disk layout (little-endian, 1024-byte blocks by default — adjustable via s_type at sb+508): Block 0 bootstrap Block 1 superblock @ file offset 1024 Block 2.. inode table (64-byte inodes, 10 direct + 1+2+3 indirect ptrs stored as 3-byte addresses) data blocks follow Superblock field of interest: u32 s_magic (sb+504) 0xFD187E20 (same magic as s5fs — distinguished from SysV/Coherent by extension) u32 s_type (sb+508) 1=512B/2=1024B/3=2048B blocks Root inode = 2. Directory entry: u16 inode + 14-char name. Spec source: SCO XENIX System V Programmer's Reference (1989) Appendix C; Linux kernel fs/sysv/super.c which historically mounted Xenix volumes via the sysv driver.

### XenixWriter

Builds minimal Microsoft/SCO Xenix System V filesystem images. Targets the "Xenix V" (s5fs-compatible) variant — the layout Linux's historical `sysv` driver mounted as `-t sysv -o xenix`: 1024-byte blocks, 64-byte inodes with 24-bit zone pointers, 16-byte directory entries (u16 inode + 14-char name), and the `0xFD187E20` superblock magic at block-relative offset 504.

Layout — every emitted image has the shape boot | sb | inode-table | data:

Scope. Files are written through the 10 direct zone slots only (max 10 * blockSize bytes per file with the default 1 KB blocks). Directories use direct zones; a directory's own entry table is laid out as a flat list of 16-byte records (".", "..", child×N) starting at offset 0 of the directory's first zone. Names longer than 14 ASCII bytes are truncated (Xenix's directory entry budget). Nested paths produce real intermediate directory inodes.

### XenixExtentMap

Reports where a System V volume's bytes are: its structures, each file's blocks under its name, and what nothing holds.

The volume had no layout to report at all, which left every layout-aware verb with nothing to work from. It tracks free space with a chained cache in the superblock rather than a bitmap, so what is taken is answered by walking the inodes — which also answers by whom, and what is left over is free.

## Storage methods

- `stored` — Stored

## Further reading

- https://github.com/torvalds/linux/tree/v6.6/fs/sysv — Linux sysv driver matching the Xenix magic (v6.6 LTS tree; removed from later kernels)
- SCO "XENIX System V" development and operations documentation (vendor manuals)
- https://en.wikipedia.org/wiki/Xenix — Wikipedia article

