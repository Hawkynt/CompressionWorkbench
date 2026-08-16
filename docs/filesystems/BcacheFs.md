# BcacheFS (`BcacheFs`)

BcacheFS Linux filesystem image — R/W (WORM, SB-validated only — fsck parity pending).

> Generated from the implementation. Edit the doc comments on the descriptor,
> reader or writer rather than this file; a test regenerates it and fails on drift.

## At a glance

| | |
|---|---|
| Category | Archive |
| Family | Archive |
| Default extension | `.bcachefs` |
| Recognised extensions | `.bcachefs` |

## Detection

| Bytes | At offset | Confidence |
|---|---|---|
| `C6 85 73 F6 66 CE 90 A9 D9 6A 60 CF 80 3D F7 EF` | 4120 | 0.85 |

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

By moving what is out of place, through `BcacheFsBlockMover`.
A run is copied and whatever records its position is rewritten, so the cost is
the bytes that actually move rather than the whole volume.

| Property | Value | Meaning |
|---|---|---|
| Repoints runs independently | yes | whether a file in several pieces can be moved one piece at a time |
| Relinks a whole allocation | no | whether a scattered file's chain can be restated in one call |
| Holds runs outside the volume | yes | whether a full volume can be rearranged by lifting a run into memory |

## How a volume is laid out

### BcacheFsFormatDescriptor

Descriptor for bcachefs volumes: a superblock at offset 4096, and b-trees under it holding the names, the metadata and the positions of every file's bytes. Volumes written here are read by the kernel driver, and read back by `BcacheFsReader` — which understands both the packed keys `mkfs.bcachefs` writes and the plain ones this project does.

Such a volume carries its allocation information — what each bucket holds, every bucket's generation, and the runs of buckets nothing was laid into — so one volume serves a read-only and a read-write mount alike, and bcachefs fsck walks it and finds nothing to fix. See `BcacheFsWriter`.

References:

### BcacheFsReader

Reads the files a bcachefs volume holds.

There is no directory to walk and no inode table to index. Names come from the dirents tree, each key of which sits at a position made of its directory's inode and a hash of the name; sizes come from the inodes tree; and the bytes come from the extents tree, whose keys are positioned by the inode and the sector one past the end of what they cover. A path is rebuilt by joining the three.

### BcacheFsWriter

Writes a bcachefs volume: a superblock, the b-trees that describe the files, and the files themselves.

bcachefs keeps no directory blocks and no inode table. A file's name is a key in the dirents tree, its metadata a key in the inodes tree, and its bytes are named by keys in the extents tree; a volume is those trees plus a superblock that says where their roots are. Because the volume is written whole and never mounted for writing in between, the roots go in the superblock's clean section, and no journal entries are needed to find them.

The allocation information is written too: the alloc tree says what each bucket holds and how much of it is used, the bucket_gens tree gives every bucket's generation, and the freespace tree covers the runs of buckets nothing has been laid into. What each bucket holds has to agree with what the extents say, and the count of b-tree buckets feeds itself — those keys are themselves keys, so adding them can want another node — which is why the description is settled by repetition rather than worked out once.

It used to be left out, and the volume claimed no_alloc_info and small_image to be let past the check that would have built it. No formatter sets either, so the volume could be told from one by the bits alone, and a read-write mount was refused outright. Neither is claimed now.

Still not written are the backpointer, LRU and accounting trees. The checker rebuilds those without complaint; if that stops being true they belong here too.

## Parameters

| Key | Kind | Default | Allowed | Meaning |
|---|---|---|---|---|
| `ImageSize` | Enum | `Auto (fit to files)` | `Auto (fit to files)`, `128 MB`, `256 MB`, `512 MB` | Total image capacity. Must be at least 128 MB so the superblock copies fit. |
| `VolumeLabel` | String | `` | any | Volume name shown by file managers (max 31 chars). |

## Storage methods

- `stored` — Stored

## Further reading

- https://bcachefs.org — official site, incl. the "Principles of Operation" on-disk documentation
- https://github.com/koverstreet/bcachefs — canonical source tree (Kent Overstreet); bcachefs_format.h defines bch_sb
- https://en.wikipedia.org/wiki/Bcachefs — Wikipedia overview

