# NILFS2 (`Nilfs2`)

NILFS2 continuous-snapshot log-structured filesystem — Create emits a kernel-mountable single-checkpoint image: a byte-accurate, CRC-valid superblock pair (primary at 1024 + backup before EOF, s_bytes=280, crc32_le-sealed s_sum, label at +0xA8) plus the full log (super root with DAT/cpfile/sufile inodes + CRC, segment summary with ss_sumsum/ss_datasum, ifile holding the root-dir inode, DAT table, flat root directory with the files). The real nilfs2 kernel driver mounts it and reads the files back (verified via the libguestfs appliance). Add/Replace/Remove append a fresh log segment at the tail and bump s_last_cno (spec-sanctioned in-place edit); prior segments stay byte-identical (continuous-snapshot invariant). The reader validates real mkfs.nilfs2 superblocks (checksum + dual-SB selection). Subdirectories / large files and multi-checkpoint snapshots remain out of scope.

> Generated from the implementation. Edit the doc comments on the descriptor,
> reader or writer rather than this file; a test regenerates it and fails on drift.

## At a glance

| | |
|---|---|
| Category | Archive |
| Family | Archive |
| Default extension | `.nilfs2` |
| Recognised extensions | `.nilfs2`, `.nilfs` |

## Detection

| Bytes | At offset | Confidence |
|---|---|---|
| `34 34` | 1030 | 0.85 |

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

By moving what is out of place, through `Nilfs2BlockMover`.
A run is copied and whatever records its position is rewritten, so the cost is
the bytes that actually move rather than the whole volume.

| Property | Value | Meaning |
|---|---|---|
| Repoints runs independently | yes | whether a file in several pieces can be moved one piece at a time |
| Relinks a whole allocation | no | whether a scattered file's chain can be restated in one call |
| Holds runs outside the volume | yes | whether a full volume can be rearranged by lifting a run into memory |

## How a volume is laid out

### Nilfs2FormatDescriptor

NILFS2 descriptor (continuous-snapshot log-structured filesystem, Linux mainline since 2.6.30). Magic 0x3434 sits at superblock+6 (file offset 1030).

R/W scope. Create emits a spec-compliant superblock plus a writer-private compact directory at offset 2048 (the base checkpoint at cno=1). Add / Replace / Remove append a fresh log segment (an appended-segment magic header + cno + dirents + payload) at the tail of the volume and bump s_last_cno in the superblock — the only in-place edit, sanctioned by the NILFS2 spec for advancing the checkpoint pointer. Every byte of every prior segment stays byte-identical at its original offset, so the older state is byte-recoverable as a snapshot (continuous-snapshot semantic).

Kernel-mountable. Create emits the full single-checkpoint log the Linux nilfs2 driver needs to mount: a super root with the DAT / cpfile / sufile inodes (+ their CRC), a segment summary with the spec ss_sumsum / ss_datasum checksums, an ifile holding the root directory inode, a DAT (Disk Address Translation) table, and a flat root directory carrying the files. A real mount -t nilfs2 mounts the image, lists the directory, and reads the files back (verified via the libguestfs appliance kernel). Subdirectories and files larger than a direct block map stay in the writer-private directory for the reader but are not materialised in the mountable tree; snapshots / multi-checkpoint chains remain out of scope.

References:

### Nilfs2Reader

Reads NILFS2 superblock metadata (Linux's continuous-snapshot log-structured filesystem, mainline since 2.6.30). Full file traversal would require walking the DAT (Disk Address Translation) B-tree and replaying log segments — multi-week work. This reader surfaces the parsed superblock + checkpoint anchor as a structured metadata bundle plus the raw image, matching the pattern used by other research/proprietary read-only FSes in this project. Superblock layout (selected, little-endian, sits at file offset 1024): 0x00 u32 s_rev_level 0x04 u16 s_minor_rev_level 0x06 u16 s_magic (must be 0x3434 = NILFS_SUPER_MAGIC) 0x08 u16 s_bytes 0x0A u16 s_flags 0x0C u32 s_crc_seed 0x10 u32 s_sum 0x14 u32 s_log_block_size 0x18 u64 s_nsegments 0x20 u64 s_dev_size 0x28 u64 s_first_data_block 0x30 u32 s_blocks_per_segment 0x34 u32 s_r_segments_percentage 0x38 u64 s_last_cno (last checkpoint number) 0x40 u64 s_last_pseg (last partial segment) ...

### Nilfs2Writer

Writes a kernel-mountable NILFS2 image. Emits the full single-checkpoint log structure the Linux `nilfs2` driver needs to mount — a super root with the DAT / cpfile / sufile inodes, a segment summary with the spec checksums, an ifile holding the root directory inode, a DAT (disk-address-translation) table, and a root directory carrying the user files — alongside the byte- accurate, CRC-valid superblock pair. A real `mount -t nilfs2` mounts the image and reads the files back (verified via the libguestfs appliance kernel).

Verified mountable. The emitted image mounts under the real kernel nilfs2 driver: the directory lists, the file contents read back, and the kernel can write new files into it. This was confirmed segment-by-segment against a real mkfs.nilfs2 reference image and gated by a guestfish mount + read-back test.

On-disk layout (4 KiB blocks shown; any legal block size works).

Why the log comes first. The sufile is a single block, so it can only describe block_size / 16 segments. With the payload ahead of the log, a volume of any size pushed the log into a segment the sufile could not address. Keeping the log at the front bounds the sufile slot for every volume size, and lets the payload be streamed rather than held in memory.

Scope. Single checkpoint (cno=1), single partial segment. A file of a few blocks is mapped by pointers written into its inode; a longer one by a b-tree of one level, whose leaves the log carries and the address table translates like any other block of the file. What bounds a volume now is the height of that tree, which grows as the file does; the address table and the inode file map themselves the same way and grow with it, the table across as many allocation groups as it needs. Half a gigabyte in one file has been read back under the kernel driver, as have twenty thousand files, and directories nested several deep and hundreds of entries wide — a directory spans as many blocks as its entries need, mapped from its inode like anything else. A name with a path in it makes the directories it implies, each holding as many entries as fit one block. The writer-private directory carries every file in full for the reader either way. Snapshots and multi-checkpoint chains are out of scope.

### Nilfs2Layout

Finds the payloads the private directory holds and the eight bytes that say where each one starts.

A payload's position is written down as an offset from the start of the segment that describes it. For the base segment that offset is a field in its directory, and moving a payload is a change to that field — provided the payload stays inside the base segment's own area.

It has to. The reader finds the first appended segment by carrying on from where the base payloads end, and each further one from where the previous segment's payloads end; a payload that reached past a segment header would hide it, and one before its own segment's payload start is a negative offset the format cannot express.

## Parameters

| Key | Kind | Default | Allowed | Meaning |
|---|---|---|---|---|
| `BlockSize` | Enum | `0` | `0`, `1024`, `2048`, `4096`, `8192`, `16384`, `32768`, `65536` | NILFS2 block size in bytes (0 = auto-optimise for least padding slack; spec allows 1024..65536). |
| `VolumeLabel` | String | `` | any | Up to 16 ASCII characters written into the superblock volume-label slot. |

## Storage methods

- `stored` — Stored

## Further reading

- https://nilfs.sourceforge.io/ — NILFS project home
- https://www.kernel.org/doc/html/latest/filesystems/nilfs2.html — kernel documentation
- https://github.com/torvalds/linux/blob/master/include/uapi/linux/nilfs2_ondisk.h — canonical on-disk structures
- https://en.wikipedia.org/wiki/NILFS — Wikipedia article

