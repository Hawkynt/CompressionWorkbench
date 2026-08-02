# ReiserFS (`ReiserFs`)

ReiserFS v3 filesystem image (R/W, full S+tree mutation via rebuild)

> Generated from the implementation. Edit the doc comments on the descriptor,
> reader or writer rather than this file; a test regenerates it and fails on drift.

## At a glance

| | |
|---|---|
| Category | Archive |
| Family | Archive |
| Default extension | `.reiserfs` |
| Recognised extensions | `.reiserfs` |

## Detection

| Bytes | At offset | Confidence |
|---|---|---|
| `52 65 49 73 45 72 46 73` | 65588 | 0.95 |
| `52 65 49 73 45 72 32 46 73` | 65588 | 0.95 |
| `52 65 49 73 45 72 33 46 73` | 65588 | 0.95 |

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

By moving what is out of place, through `ReiserFsBlockMover`.
A run is copied and whatever records its position is rewritten, so the cost is
the bytes that actually move rather than the whole volume.

| Property | Value | Meaning |
|---|---|---|
| Repoints runs independently | yes | whether a file in several pieces can be moved one piece at a time |
| Relinks a whole allocation | no | whether a scattered file's chain can be restated in one call |
| Holds runs outside the volume | yes | whether a full volume can be rearranged by lifting a run into memory |

## How a volume is laid out

### ReiserFsFormatDescriptor

R/W descriptor for ReiserFS v3.6 filesystem images (superblock at offset 65536, R5 directory hash, 4 KB blocks). References:

### ReiserFsReader

Reads a ReiserFS v3 filesystem image. Field offsets follow the Linux kernel `struct reiserfs_super_block` (see `ReiserFsWriter` for the full offset table).

### ReiserFsWriter

Writes a SPEC-COMPLIANT ReiserFS v3.6 filesystem image. Multi-leaf S+tree with internal pages, R5-hashed dirents, DIRECT items for small file bodies and INDIRECT items (block-pointer arrays referencing dedicated data blocks laid out past the tree) for large bodies. Layout matches what reiserfsprogs' make_sure_root_dir_exists + reiserfs_add_entry would produce.

Kernel-reference offsets inside the 65536-byte-aligned superblock: `0 + 4 s_block_count 4 + 4 s_free_blocks 8 + 4 s_root_block 12 + 32 s_journal (journal_params, 8 × __le32) 44 + 2 s_blocksize 46 + 2 s_oid_maxsize 48 + 2 s_oid_cursize 50 + 2 s_umount_state 52 + 10 s_magic 62 + 2 s_fs_state 64 + 4 s_hash_function_code 68 + 2 s_tree_height 70 + 2 s_bmap_nr 72 + 2 s_version 74 + 2 s_reserved_for_journal 76 + 4 s_inode_generation 80 + 4 s_flags 84 + 16 s_uuid 100 + 16 s_label 116 + 2 s_mnt_count 118 + 2 s_max_mnt_count 120 + 4 s_lastcheck 124 + 4 s_check_interval 128 + 76 s_unused 204 + .. objectid_map (packed pairs, cursize × 4 bytes)` Every block_head is 24 bytes: blk_level(2) + blk_nr_item(2) + blk_free_space(2) + blk_reserved(2) + blk_right_delim_key(16).

## Parameters

| Key | Kind | Default | Allowed | Meaning |
|---|---|---|---|---|
| `VolumeLabel` | String | `` | any | Volume name shown by file managers (max 16 chars). |

## Storage methods

- `stored` — Stored

## Further reading

- https://github.com/torvalds/linux/tree/v6.6/fs/reiserfs — Linux reference implementation (v6.6 LTS tree; the driver was removed from later kernels)
- reiserfsprogs (mkreiserfs / debugreiserfs) — canonical userspace tooling
- https://en.wikipedia.org/wiki/ReiserFS — Wikipedia article

