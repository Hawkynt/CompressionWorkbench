# UNIX System V FS (`SysV`)

AT&T UNIX System V s5fs filesystem image — true in-place R/W (spec-audited writer + SysVInPlaceModifier mutating inode table and data blocks at fixed byte offsets via the chained free-block group cache + s_inode[100] cache with re-scan refill; Linux sysv kernel driver mountable when host ships sysv.ko).

> Generated from the implementation. Edit the doc comments on the descriptor,
> reader or writer rather than this file; a test regenerates it and fails on drift.

## At a glance

| | |
|---|---|
| Category | Archive |
| Family | Archive |
| Default extension | `.s5` |
| Recognised extensions | `.s5`, `.sysv` |

## Detection

| Bytes | At offset | Confidence |
|---|---|---|
| `20 7E 18 FD` | 1016 | 0.90 |

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

By moving what is out of place, through `SysVBlockMover`.
A run is copied and whatever records its position is rewritten, so the cost is
the bytes that actually move rather than the whole volume.

| Property | Value | Meaning |
|---|---|---|
| Repoints runs independently | yes | whether a file in several pieces can be moved one piece at a time |
| Relinks a whole allocation | no | whether a scattered file's chain can be restated in one call |
| Holds runs outside the volume | yes | whether a full volume can be rearranged by lifting a run into memory |

## How a volume is laid out

### SysVFormatDescriptor

R/W descriptor for AT&amp;T UNIX System V (s5fs) filesystem images. Magic `0xFD187E20` at file offset 1024+504 = 0x5F8. References:

Reads any s5fs image with the documented superblock layout (1024-byte blocks, 64-byte inodes, 24-bit zone pointers, 16-byte directory entries). Writes a fresh image targeting the same classic AT&T variant only — other in-the-wild SysV-family flavours (Coherent, Xenix, SCO, AFS) use distinct magics and inode shapes and are out of scope for the writer.

Mutation surface (`IArchiveModifiable`): true in-place R/W via `SysVInPlaceModifier` — every Add/Remove/Replace mutates the existing image at fixed byte offsets without rebuilding, including the classic V7/SYSV chained free-block group cache (refill from chain when s_nfree drops to 1; spill to a new chain block when it would exceed 50) and the in-line s_inode[100] cache with re-scan refill. Nested-path adds/removes fall back to the rebuild-from-scratch path so the in-place engine never has to re-walk the directory tree. Per-file size is bounded at 10 direct zones (10 KB); indirect blocks are out of scope (same as the WORM writer).

Acceptance gates: round-trip via our own reader (necessary), spec field-offset audit against linux/fs/sysv/super.c and the AT&T System V Interface Definition (sufficient — the writer comments cite the exact offsets), and an opt-in WSL mount -t sysv -o loop,ro gate that skips cleanly when the kernel's sysv driver isn't loadable (the default WSL2 kernel ships without it).

### SysVReader

Reader for AT&amp;T Bell Labs UNIX System V "s5fs" filesystem (1983, distinguished from BSD's UFS). On-disk layout (little-endian; documented in AT&amp;T System V Interface Definition and in linux/fs/sysv/super.c): Block 0 bootstrap (ignored) Block 1 superblock (1024 bytes at file offset 0x400) Block 2.. inode list ("ilist") block N.. data blocks Superblock layout (offsets from block-start; we read the magic at +504 i.e. file offset 1024+504 = 0x5F8): u16 s_isize (0) size of ilist in blocks u32 s_fsize (2) total blocks in volume u16 s_nfree (6) number of free blocks in inline cache u32 s_free[50] (8) free-block cache (208 bytes) u16 s_ninode (216) number of free inodes in inline cache u16 s_inode[100] (218) free-inode cache u8 s_flock (418) u8 s_ilock (419) u8 s_fmod (420) u8 s_ronly (421) u32 s_time (422) timestamp ... u32 s_magic (504) magic number 0xFD187E20 for s5fs u32 s_type (508) block-size code: 1=512B, 2=1024B, 3=2048B Inode (64 bytes — System V uses 64-byte inodes, larger than Minix's 32): u16 di_mode (0) u16 di_nlink (2) u16 di_uid (4) u16 di_gid (6) u32 di_size (8) u8 di_addr[40] (12) thirteen 3-byte block addresses (10 direct, 1 indirect, 1 double-indirect, 1 triple-indirect) u32 di_atime (52) u32 di_mtime (56) u32 di_ctime (60) Directory entries are 16 bytes (ino:u16, name:14). Root inode is inode 2.

### SysVWriter

Builds minimal AT&amp;T UNIX System V "s5fs" filesystem images (the classic 1983 layout — distinguished from BSD UFS and from Linux's "Coherent" / "Xenix" SysV variants by magic `0xFD187E20` and type code 2 = 1024-byte blocks).

Layout (every field offset cross-checked against linux/fs/sysv/super.c and the AT&T System V Interface Definition):

`Block 0 bootstrap (zeroed) Block 1 superblock (1024 bytes at file offset 0x400) u16 s_isize [ +0] ilist size in blocks u32 s_fsize [ +2] total blocks on device u16 s_nfree [ +6] free-block cache count u32 s_free[50] [ +8] free-block cache u16 s_ninode [+216] free-inode cache count u16 s_inode[100] [+218] free-inode cache u8 s_flock [+418] locks (zero on a clean fs) u8 s_ilock [+419] u8 s_fmod [+420] superblock-modified flag (clean=0) u8 s_ronly [+421] read-only flag (0) u32 s_time [+422] last-update timestamp u16 s_dinfo[4] [+426] device info (zero) u32 s_tfree [+434] total free blocks u16 s_tinode [+438] total free inodes u8 s_fname[6] [+440] u8 s_fpack[6] [+446] ... (zeros) u32 s_magic [+504] 0xFD187E20 u32 s_type [+508] 1=512B 2=1024B 3=2048B Block 2..N inode list ("ilist"), 64-byte inodes u16 di_mode [ +0] u16 di_nlink [ +2] u16 di_uid [ +4] u16 di_gid [ +6] u32 di_size [ +8] u8 di_addr[40] [+12] 13 x 3-byte zone ptrs (10 direct, 1 ind, 1 dind, 1 tind) u32 di_atime [+52] u32 di_mtime [+56] u32 di_ctime [+60] Block N+1.. data blocks`

Free-block management uses the classic chained 50-pointer cache. The in-superblock cache holds up to 50 entries; when full and another block is freed (which doesn't happen at format time but is how the kernel later extends the chain), the kernel writes s_nfree + s_free[] into the about-to-be-freed block and resets the cache to count 1, leaving the newly freed block at the cache head. At format time the entire free chain is encoded by leaving the head pointer in the superblock and chaining out through additional 1024-byte free-list blocks (each laid as u16 nfree; u8 pad[2]; u32 free[50] — the 2-byte pad keeps the array 4-byte aligned, matching how Linux's fs/sysv/balloc.c reads them).

The writer targets the classic System V variant only: 1024-byte blocks, 16-byte directory entries (inum:u16 + name:char[14]), little-endian field ordering. Other in-the-wild SysV-family variants (Coherent, Xenix, SCO, AFS) carry different magics and/or different inode shapes — supporting them is out of scope.

### SysVExtentMap

Reports where a System V volume's bytes are: its structures, each file's blocks under its name, and what nothing holds.

The volume had no layout to report at all, which left every layout-aware verb with nothing to work from. It tracks free space with a chained cache in the superblock rather than a bitmap, so what is taken is answered by walking the inodes — which also answers by whom, and what is left over is free.

## Parameters

| Key | Kind | Default | Allowed | Meaning |
|---|---|---|---|---|
| `VolumeLabel` | String | `` | any | s5fs volume name stored in s_fname (max 6 ASCII chars). |

## Storage methods

- `stored` — Stored

## Further reading

- https://github.com/torvalds/linux/tree/v6.6/fs/sysv — Linux sysv driver (v6.6 LTS tree; the driver was removed from later kernels)
- Maurice J. Bach, "The Design of the UNIX Operating System" (Prentice Hall, 1986) — s5fs internals
- AT&T "System V Interface Definition"

