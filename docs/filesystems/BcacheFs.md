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

What such a volume does not carry is allocation information: the trees a running filesystem keeps so it can decide where to write next. bcachefs's own image tooling leaves them out too, and rebuilds them on the first read-write mount. Which of the two mounts a volume is written for is an option; the default is reading. See `BcacheFsWriter`.

References:

### BcacheFsReader

Reads the files a bcachefs volume holds.

There is no directory to walk and no inode table to index. Names come from the dirents tree, each key of which sits at a position made of its directory's inode and a hash of the name; sizes come from the inodes tree; and the bytes come from the extents tree, whose keys are positioned by the inode and the sector one past the end of what they cover. A path is rebuilt by joining the three.

### BcacheFsWriter

Writes a bcachefs volume: a superblock, the b-trees that describe the files, and the files themselves.

bcachefs keeps no directory blocks and no inode table. A file's name is a key in the dirents tree, its metadata a key in the inodes tree, and its bytes are named by keys in the extents tree; a volume is those trees plus a superblock that says where their roots are. Because the volume is written whole and never mounted for writing in between, the roots go in the superblock's clean section, and no journal entries are needed to find them.

What is not written is the allocation information — the alloc, freespace, backpointer and accounting trees a running filesystem keeps so it can decide where to put the next write. A volume written whole does not have them, and the two mounts want opposite things of that: a read-only mount has to be told not to go and build them, because building them is a write, while a read-write mount has to be allowed to, because it cannot allocate without them. The format has a bit for each case and they are mutually exclusive, so which one a volume gets is a choice — see `SetReadWriteCapable`. By default it is the first, because a volume written whole is an image, and such a volume mounts read-only and passes the format's own checker.

## Parameters

| Key | Kind | Default | Allowed | Meaning |
|---|---|---|---|---|
| `ImageSize` | Enum | `Auto (fit to files)` | `Auto (fit to files)`, `128 MB`, `256 MB`, `512 MB` | Total image capacity. Must be at least 128 MB so the superblock copies fit. |
| `MountFor` | Enum | `Reading` | `Reading`, `Writing` | A volume written whole has no allocation information, and the two mounts want opposite things of that. Reading: the volume says it is an image file, and a read-only mount takes it as it is. Writing: a read-write mount rebuilds the allocation information on the way in, and a read-only mount no longer works. |
| `VolumeLabel` | String | `` | any | Volume name shown by file managers (max 31 chars). |

## Storage methods

- `stored` — Stored

## Further reading

- https://bcachefs.org — official site, incl. the "Principles of Operation" on-disk documentation
- https://github.com/koverstreet/bcachefs — canonical source tree (Kent Overstreet); bcachefs_format.h defines bch_sb
- https://en.wikipedia.org/wiki/Bcachefs — Wikipedia overview

