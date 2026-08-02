# QNX4 FS (`Qnx4`)

QNX4 filesystem image (1991-2001, QNX Software Systems) — R/W (flat root, max 29 user files).

> Generated from the implementation. Edit the doc comments on the descriptor,
> reader or writer rather than this file; a test regenerates it and fails on drift.

## At a glance

| | |
|---|---|
| Category | Archive |
| Family | Archive |
| Default extension | `.qnx4` |
| Recognised extensions | `.qnx4`, `.qnx` |

## Detection

| Bytes | At offset | Confidence |
|---|---|---|
| `01` | 573 | 0.35 |
| `08` | 573 | 0.35 |
| `09` | 573 | 0.40 |

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

By moving what is out of place, through `Qnx4BlockMover`.
A run is copied and whatever records its position is rewritten, so the cost is
the bytes that actually move rather than the whole volume.

| Property | Value | Meaning |
|---|---|---|
| Repoints runs independently | no | whether a file in several pieces can be moved one piece at a time |
| Relinks a whole allocation | no | whether a scattered file's chain can be restated in one call |
| Holds runs outside the volume | no | whether a full volume can be rearranged by lifting a run into memory |

## How a volume is laid out

### Qnx4FormatDescriptor

R/W descriptor for QNX4 filesystem images. QNX4 has no fixed magic at the start of the image — detection relies on the inode status byte pattern in the root directory cluster (block 1).

Add / Remove are routed through `Qnx4Modifier`, which mutates the root cluster (LBA 1-4) and the .bitmap (LBA 5) in place. Scope stays flat-root (29 user files) — past that Add throws `NotSupportedException`, matching the WORM writer's capacity guard. Subdirectory emission is still out of scope.

References:

### Qnx4Reader

Reader for the QNX4 file system (1991-2001, QNX Software Systems Inc.). QNX4 uses 512-byte blocks and represents each file as a chain of contiguous extents — each extent is described by an `xtnt_t` record (first block + block count). On-disk layout (little-endian): Block 0 boot sector (variable signature) Block 1 root directory cluster (4 blocks of 64-byte inode entries) Inode entry (64 bytes per linux/fs/qnx4/qnx4.h's qnx4_inode_entry): +0x00 di_fname 16 bytes ASCII filename +0x10 di_size 4 bytes (LE) file size in bytes +0x14 di_first_xtnt 8 bytes — extent record: u32 xtnt_blk first block of extent u32 xtnt_size block count of extent +0x1C di_num_xtnts 4 bytes (LE) extra extent count +0x20 di_mode 2 bytes mode (uid|gid|perm) +0x22 di_uid 2 bytes +0x24 di_gid 2 bytes +0x26 di_ftime 4 bytes time +0x2A di_mtime 4 bytes +0x2E di_atime 4 bytes +0x32 di_ctime 4 bytes +0x36 di_zero 6 bytes +0x3C di_type 1 byte +0x3D di_status 1 byte file status (0x08=ACTIVE, 0x04=USED, 0x01=DAMAGED, 0x02=DESTROY) Spec source: linux/fs/qnx4/{qnx4.h,inode.c,namei.c} — kernel-side QNX4 driver maintained from 2.4 through 5.10.

### Qnx4Writer

Emits valid QNX4 file-system images (WORM — write-once, no in-place mutation). On-disk layout (matching what the Linux qnx4 driver expects): `Block 0 boot block (zeroed; 512 bytes) Blocks 1-4 root directory cluster (4 contiguous blocks, 32 × 64-byte inode entries). Entry 0 is the root inode pointing to itself (xtnt=blk1+4); entries 1..2 are the QNX4 system files ".bitmap" and ".inodes"; entries 3..N are user files. Block 5 ".bitmap" (block allocation bitmap, 1 bit per block, LSB first; reserved/used blocks marked). Block 6 ".inodes" (additional inode storage, zeroed — we keep all user inodes inline in the root cluster). Block 7.. user file data, each file gets a single contiguous extent rounded up to whole 512-byte blocks.`

Inode status byte (offset 0x3D in 64-byte entry):

We use `0x01` for plain user files (16-byte short names) and `0x09` (USED|LINK) for the root inode itself — this matches the on-disk pattern produced by historical QNX4 systems and what the Linux `qnx4` driver validates.

Spec source: linux/fs/qnx4/{qnx4.h,inode.c,dir.c,namei.c}.

## Storage methods

- `stored` — Stored

## Further reading

- https://github.com/torvalds/linux/blob/master/include/uapi/linux/qnx4_fs.h — canonical on-disk structures
- https://github.com/torvalds/linux/tree/master/fs/qnx4 — Linux reference implementation
- https://en.wikipedia.org/wiki/QNX — Wikipedia article

