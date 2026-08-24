# UBIFS (`Ubifs`)

Unsorted Block Image File System (Linux raw-flash) — linear log scan w/ zlib data nodes; Create emits superblock+master+inode+dentry+data, Add/Replace/Remove append journal-style nodes at the journal head (committed nodes byte-identical; self-round-trip only — full TNC/LPT commit out of scope).

> Generated from the implementation. Edit the doc comments on the descriptor,
> reader or writer rather than this file; a test regenerates it and fails on drift.

## At a glance

| | |
|---|---|
| Category | Archive |
| Family | Archive |
| Default extension | `.ubifs` |
| Recognised extensions | `.ubifs`, `.ubi`, `.img` |

## Detection

| Bytes | At offset | Confidence |
|---|---|---|
| `31 18 10 06` | 0 | 0.35 |

## Verbs

| Verb | Offered | What it does |
|---|---|---|
| list / extract | yes | read the volume and copy files out of it |
| create | yes | write a fresh volume holding the given files |
| add / remove | yes | change a volume in place |
| defragment | yes | lay the volume out again |
| wipe free space | no | zero what no file holds |
| shrink | yes | reduce the volume to what it needs |
| optimise layout | yes | re-lay the volume at a chosen geometry |
| report layout | yes | say where every byte belongs |
| move blocks | no | relocate a run and repoint what names it |
| move metadata | no | relocate the volume's own structures |

### How it defragments

By moving what is out of place, through `UbifsBlockMover`.
A run is copied and whatever records its position is rewritten, so the cost is
the bytes that actually move rather than the whole volume.

| Property | Value | Meaning |
|---|---|---|
| Repoints runs independently | yes | whether a file in several pieces can be moved one piece at a time |
| Relinks a whole allocation | no | whether a scattered file's chain can be restated in one call |
| Holds runs outside the volume | yes | whether a full volume can be rearranged by lifting a run into memory |

## How a volume is laid out

### UbifsFormatDescriptor

UBIFS (Unsorted Block Image File System) descriptor. Read path: triage artifacts (passthrough, node-counts metadata, flat inode + dentry tables) plus real per-file extraction via linear log scan with zlib / stored DATA-node support. Write path (R/W): emits a flat sequence of superblock + master + inode + dentry + zlib-compressed data nodes for Create, and appends fresh INO / DENT / DATA nodes at the journal head for Add / Replace / Remove. Committed nodes stay byte-identical at their original offsets — the kernel-style log-structured invariant (no in-place rewrites until commit-merge) is preserved. Full TNC / LPT commit pipeline (required for kernel mount) is multi-week work and remains out of scope. References:

Why this volume is laid out again by rebuilding rather than by moving.

Nothing here reads the index. A file's data nodes are found by scanning the image for node magic, not by walking the tree that records where they are — the TNC that indexes them and the LPT that accounts for each erase block are not decoded at all. So there is no field to repoint: what would have to be rewritten for a moved node to be found again is a structure this implementation cannot yet read, let alone write.

### UbifsFileReader

Reads a UBIFS image and extracts file contents by linearly scanning the log for inode, data, and dentry nodes, replaying them in sequence-number (sqnum) order, and reassembling each file from its DATA blocks.

What this reader handles: stored (uncompressed) DATA blocks, zlib-compressed DATA blocks (the UBIFS default), inode size/mode metadata, dentry parent/name/target tuples, recursive path reconstruction from the dentry tree.

What's NOT covered: (less common — these images return empty per-block payload with a TODO marker in metadata); TNC / LPT / wandering-tree traversal (we use a linear log scan instead, which is correct for normal UBIFS images but may miss versions in pathological recovery scenarios); xattrs; hardlinks beyond first-seen.

UBIFS key layout: 16 bytes. Lower 32 bits = inode number (LE). Upper 32 bits at offset 4 hold type in the top 3 bits and a per-type value (block index for DATA, dirent-hash for DENT) in the low 29 bits.

### UbifsWriter

Builds a minimal UBIFS image holding a flat list of small regular files plus the directory tree needed to reach them.

What this writer emits: a linear node stream — superblock, master, root-directory inode, per-file inode + dentry + zlib-compressed DATA node(s). Each node carries a fully valid 24-byte common header (magic, CRC-32 over the payload after the CRC field, sqnum, len, type, group=0) so the same linear scanner that drives `UbifsFileReader` can round-trip the image.

What's NOT emitted (out of scope — these require a full wandering-tree commit pipeline and a kernel-mountable image is a multi-week project): LPT (LEB Properties Tree), TNC (Tree Node Cache) index B+tree, commit-start / reference / orphan nodes, journal heads, padding/garbage-collection markers. A real mkfs.ubifs wires these together so the kernel can mount the result; our reader operates on a linear log scan and does not need them. Tests therefore validate self-round-trip, not kernel mount.

Compression: DATA nodes are zlib (DEFLATE) compressed when that shrinks the payload, otherwise stored. LZO/ZSTD are not emitted.

### UbifsLayout

Finds every node in a UBIFS image and says which file's bytes it carries.

What this writer emits is a linear log of nodes and nothing else — no index tree, no erase-block accounting, no journal heads. The reader replays that log, taking the highest sequence number for each inode and block. So a node's position is recorded nowhere at all: it is found by looking for the magic at the head of it.

That is what makes a node movable without repointing anything. What it does mean is that the bytes left behind have to go: a copy of a node still carrying its magic is a second node, and the log would replay both.

## Parameters

| Key | Kind | Default | Allowed | Meaning |
|---|---|---|---|---|
| `LebSize` | Enum | `64 KB` | `Auto`, `4 KB`, `8 KB`, `16 KB`, `32 KB`, `64 KB`, `128 KB`, `256 KB`, `512 KB`, `1 MB` | Logical erase-block size. Written to the superblock and used to pad each LEB; 64 KB matches common NAND flash. |

## Storage methods

- `stored` — Stored

## Further reading

- http://www.linux-mtd.infradead.org/doc/ubifs.html — MTD project UBIFS documentation — the canonical design doc
- https://github.com/torvalds/linux/blob/master/fs/ubifs/ubifs-media.h — canonical on-disk node formats
- https://www.kernel.org/doc/html/latest/filesystems/ubifs.html — kernel documentation
- https://en.wikipedia.org/wiki/UBIFS — Wikipedia article

