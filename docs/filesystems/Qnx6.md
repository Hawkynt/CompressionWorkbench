# QNX6 Neutrino FS (`Qnx6`)

QNX6 Neutrino filesystem — R/W (paired superblocks; reader walks a single-block directory and direct-extent files; Add/Remove mutate in place with synchronous dual-superblock mirror).

> Generated from the implementation. Edit the doc comments on the descriptor,
> reader or writer rather than this file; a test regenerates it and fails on drift.

## At a glance

| | |
|---|---|
| Category | Archive |
| Family | Archive |
| Default extension | `.qnx6` |
| Recognised extensions | `.qnx6` |

## Detection

| Bytes | At offset | Confidence |
|---|---|---|
| `22 11 19 68` | 8192 | 0.95 |

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

By moving what is out of place, through `Qnx6BlockMover`.
A run is copied and whatever records its position is rewritten, so the cost is
the bytes that actually move rather than the whole volume.

| Property | Value | Meaning |
|---|---|---|
| Repoints runs independently | no | whether a file in several pieces can be moved one piece at a time |
| Relinks a whole allocation | no | whether a scattered file's chain can be restated in one call |
| Holds runs outside the volume | yes | whether a full volume can be rearranged by lifting a run into memory |

## How a volume is laid out

### Qnx6FormatDescriptor

Descriptor for QNX6 (Neutrino) filesystem images. Magic 0x68191122 (LE) at file offset 0x2000. Read + R/W (Add/Remove): the writer (`Qnx6Writer`) emits paired superblocks (primary at 0x2000 + identical secondary mirror at the tail of the volume) — the power-safe contract — alongside a flat 128-byte inode array and 32-byte directory entries. The modifier (`Qnx6Modifier`) mutates that layout in place and re-mirrors the superblock to the new tail after each Add/Remove so the dual-superblock pairing remains byte-identical. Self-round-trips through `Qnx6Reader`. References:

### Qnx6Reader

Reader for the QNX6 ("Neutrino") file system. QNX6 has a layered design: two superblocks at fixed offsets (primary at 0x2000, secondary at the end of the volume) for consistency checking, and a B-tree of "rootnodes" pointing to inode and longfilename data. On-disk layout (little-endian): Block 0 bootblock (8 KiB reserved) 0x2000 primary superblock (qnx6_super_block — 512 bytes) ... inode tree, data blocks Superblock (qnx6_super_block, linux/fs/qnx6/qnx6.h): +0x00 sb_magic u32 0x68191122 +0x04 sb_checksum u32 +0x08 sb_serial u64 +0x10 sb_ctime u32 creation time +0x14 sb_atime u32 last mount time +0x18 sb_flags u32 +0x1C sb_version1 u16 +0x1E sb_version2 u16 +0x20 sb_volumeid 16 volume UUID +0x30 sb_blocksize u32 e.g. 1024 +0x34 sb_num_inodes u32 +0x38 sb_free_inodes u32 +0x3C sb_num_blocks u32 +0x40 sb_free_blocks u32 +0x44 sb_num_levels u16 tree depth +0x46 sb_indir_levs u16 +0x48 sb_inode_root qnx6_root_node (40 bytes — size + 16 ptrs + 4 levels) Inode (128 bytes per qnx6_inode_entry): +0x00 di_size u64 +0x08 di_uid u32 +0x0C di_gid u32 +0x10 di_ftime u32 +0x14 di_mtime u32 +0x18 di_atime u32 +0x1C di_ctime u32 +0x20 di_mode u16 +0x22 di_ext_mode u16 +0x24 di_block_ptr[16] u32 direct +0x64 di_filelevels u8 +0x65 di_status u8 +0x66 di_unknown 14 Spec source: linux/fs/qnx6/{qnx6.h,super.c,inode.c,dir.c} (driver since kernel 2.6.39).

### Qnx6Writer

WORM writer for QNX6 (Neutrino) filesystem images. Emits a power-safe layout: the primary superblock at file offset 0x2000 plus an identical secondary mirror at the last 512 bytes of the volume. The dual-superblock pairing is the safety contract — a torn write to one copy leaves the other intact. On-disk image laid down by `Build`: `0x0000..0x1FFF boot region (zeroed) 0x2000..0x21FF primary superblock (qnx6_super_block, 512 B) block 16 (0x4000..) inode table (capacity-sized; 128 B per inode) block 17 (0x4400..) root directory data block (32-B dirents) block 18..N file data, one contiguous extent per file last 512 B of file secondary (mirror) superblock` Inode layout matches `Qnx6Reader`: inode 1 = root directory (size = bytes of dirents, first ptr = root dir block) inode 2..1+N = files (size = file length, first ptr = first data block) Field encoding is little-endian to match the reader's `MagicQnx6` probe (0x68191122 LE). Constraints (matching reader capability — see `Qnx6Reader`): • The reader walks a single directory block, so the writer caps the root directory at ⌊blockSize/32⌋ = 32 entries. • The reader skips dirents whose name_len &gt; 27. The writer enforces that cap up front — entries with longer names are skipped, mirroring the reader's behaviour. (QNX6's longfile-pointer dirent form is documented in the spec but unreadable through the current Stage-1 reader, so emitting it would yield silently-dropped entries on round-trip.) • Files larger than one block are laid down as one contiguous run starting at the file's first-direct block pointer; the reader's Extract path reads `entry.Size` bytes from that offset, which spans the whole run.

## Storage methods

- `stored` — Stored

## Further reading

- https://www.kernel.org/doc/html/latest/filesystems/qnx6.html — kernel documentation of the on-disk layout (dual superblocks)
- https://github.com/torvalds/linux/tree/master/fs/qnx6 — Linux reference implementation
- QNX Neutrino fs-qnx6.so documentation (QNX Software Systems)

