# VxFS (Veritas) (`VxFs`)

VxFS (Veritas File System) volume — files, and a layout pass over them.

> Generated from the implementation. Edit the doc comments on the descriptor,
> reader or writer rather than this file; a test regenerates it and fails on drift.

## At a glance

| | |
|---|---|
| Category | Archive |
| Family | Archive |
| Default extension | `.vxfs` |
| Recognised extensions | `.vxfs` |

## Detection

| Bytes | At offset | Confidence |
|---|---|---|
| `F5 FC 01 A5` | 1024 | 0.90 |
| `A5 01 FC F5` | 1024 | 0.90 |

## Verbs

| Verb | Offered | What it does |
|---|---|---|
| list / extract | yes | read the volume and copy files out of it |
| create | yes | write a fresh volume holding the given files |
| add / remove | no | change a volume in place |
| defragment | yes | lay the volume out again |
| wipe free space | yes | zero what no file holds |
| shrink | no | reduce the volume to what it needs |
| optimise layout | no | re-lay the volume at a chosen geometry |
| report layout | yes | say where every byte belongs |
| move blocks | no | relocate a run and repoint what names it |
| move metadata | no | relocate the volume's own structures |

### How it defragments

By moving what is out of place, through `VxFsBlockMover`.
A run is copied and whatever records its position is rewritten, so the cost is
the bytes that actually move rather than the whole volume.

| Property | Value | Meaning |
|---|---|---|
| Repoints runs independently | yes | whether a file in several pieces can be moved one piece at a time |
| Relinks a whole allocation | no | whether a scattered file's chain can be restated in one call |
| Holds runs outside the volume | yes | whether a full volume can be rearranged by lifting a run into memory |

## How a volume is laid out

### VxFsFormatDescriptor

Read-only descriptor for VxFS (Veritas File System), used by HP-UX, Solaris, and AIX (and a Linux read-only port). Walking the OLT (Object Location Table) → FSH (FileSet Header) → IAU (Inode Allocation Unit) chain to extract user files is explicitly out of scope (multi-week effort) — this descriptor surfaces: Detection: 4-byte magic `0xA501FCF5` at offset 1024. The magic is stored in the natural endianness of the host that wrote the volume — little-endian on x86 / Linux, big-endian on HP-UX PA-RISC and Solaris SPARC. Both signature variants are registered. Create / Modify / Defragment: `NotSupportedException` — the descriptor is read-only. References:

The walk to the files is implemented in `VxFsVolume` and the volumes this writes are mounted by the kernel's own freevxfs driver, so the superblock surface above is no longer all there is: files are listed, extracted, written and laid out again.

What is written is the plainest shape the driver accepts — one fileset, direct extents only, a flat root directory. Immediate data, extent trees and subdirectories are shapes it reads and this does not write.

### VxFsReader

Parses the VxFS (Veritas File System / VERITAS / Symantec / now part of Veritas Storage Foundation) on-disk superblock. The structure layout below tracks `fs/freevxfs/vxfs.h` in the Linux kernel (Christoph Hellwig's read-only freevxfs driver) and HP-UX VxFS documentation. Layout summary: Superblock leading fields (per `vxfs.h`): `struct vxfs_sb { uint32_t vs_magic; // offset 0: 0xA501FCF5 int32_t vs_version; // offset 4: VxFS version (1..10 documented) uint32_t vs_mtime; // offset 8: last modification time (Unix time) uint32_t vs_ctime; // offset 12: creation time int32_t vs_old_logstart; int32_t vs_old_logend; int32_t vs_bsize; // offset 24: block size (512, 1024, 2048, 4096, 8192) int32_t vs_size; // offset 28: filesystem size in blocks int32_t vs_dsize; // offset 32: data zone size in blocks uint32_t vs_old_ninode; int32_t vs_old_nau; // offset 40: number of allocation units (old IAU) int32_t vs_old_defiextsize; int32_t vs_old_ilbsize; int32_t vs_immedlen; // offset 52: max immediate-data length (typically 96) int32_t vs_ndaddr; // offset 56: number of direct addresses (10) int32_t vs_firstau; // offset 60: first allocation-unit block ... };` References:

### VxFsWriter

Builds a VxFS volume the Linux `freevxfs` driver mounts.

The driver reaches the files by a chain of five hops, and a volume is only a volume if every one of them lands. The superblock names an object location table; the table names a fileset-header inode and the block a raw inode array starts at; that inode describes a file holding two fileset headers, one structural and one primary; the structural one names the inode describing the structural inode list, and the primary one names — inside that list — the inode describing the list the user's files live in. Only then is inode 2 the root directory.

So the layout below is not a choice of taste. The raw inode array has to hold the three structural inodes at the offsets the driver computes from their numbers, the fileset-header file has to be two pages long because the driver asks for the second header by page index, and the block size has to be 1024 because that is what the driver mounted with before it knew ours — it converts the table's block number with the ratio between the two, and only a ratio of one puts the table where we wrote it.

Files are laid out in whole blocks, each as a run of direct extents. Ten fit in an inode, which is the ceiling on how many pieces one file may be in.

### VxFsExtentMap

Describes a VxFS volume block by block: what the walk to the files needs, what each file owns, and what is left.

Everything the driver reads before it reaches a file is reserved here — the superblock, the object location table, the raw inode array, the fileset headers, both inode lists and the root directory. A file moved onto any of them would be a volume that no longer mounts, which is a harder failure than a fragmented one.

### VxFsLayout

The constants and byte offsets of the VxFS structures this writes and reads.

These track fs/freevxfs/ in the Linux kernel — Christoph Hellwig's read-only driver, as revised by Krzysztof Blaszkowski in 2016 — because that driver is what decides whether a volume we emit is a VxFS volume or merely one that resembles it. Every offset below is a field the driver reads.

Two of them were wrong here before. The superblock carries two unused words between vs_cutime and vs_old_logstart, which the older notes in this project omitted, so every field from there on was read eight bytes early — the block size came out of vs_old_logstart. Nothing noticed, because nothing read past the superblock.

## Storage methods

- `stored` — Stored

## Further reading

- FULL.vxfs — the raw image bytes
- metadata.ini — parsed superblock fields
- superblock.bin — 1 KB capture of the on-disk superblock
- Linux kernel fs/freevxfs/vxfs.h + vxfs_super.c
- HP-UX "VxFS Administrator's Guide" (Veritas / Symantec)
- Wikipedia "Veritas File System"

