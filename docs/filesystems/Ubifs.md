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
| report layout | no | say where every byte belongs |
| move blocks | no | relocate a run and repoint what names it |
| move metadata | no | relocate the volume's own structures |

### How it defragments

By rebuilding: every file is read out and a fresh volume is written in the
order the requested layout asks for. Correct, but it costs the whole payload.

## How a volume is laid out

### UbifsFormatDescriptor

UBIFS (Unsorted Block Image File System) descriptor. Read path: triage artifacts (passthrough, node-counts metadata, flat inode + dentry tables) plus real per-file extraction via linear log scan with zlib / stored DATA-node support. Write path (R/W): emits a flat sequence of superblock + master + inode + dentry + zlib-compressed data nodes for Create, and appends fresh INO / DENT / DATA nodes at the journal head for Add / Replace / Remove. Committed nodes stay byte-identical at their original offsets — the kernel-style log-structured invariant (no in-place rewrites until commit-merge) is preserved. Full TNC / LPT commit pipeline (required for kernel mount) is multi-week work and remains out of scope. References:

### UbifsFileReader

Reads a UBIFS image and extracts file contents by linearly scanning the log for inode, data, and dentry nodes, replaying them in sequence-number (sqnum) order, and reassembling each file from its DATA blocks.

What this reader handles: stored (uncompressed) DATA blocks, zlib-compressed DATA blocks (the UBIFS default), inode size/mode metadata, dentry parent/name/target tuples, recursive path reconstruction from the dentry tree.

What's NOT covered: LZO and ZSTD compression (less common — these images return empty per-block payload with a TODO marker in metadata); TNC / LPT / wandering-tree traversal (we use a linear log scan instead, which is correct for normal UBIFS images but may miss versions in pathological recovery scenarios); xattrs; hardlinks beyond first-seen.

UBIFS key layout: 16 bytes. Lower 32 bits = inode number (LE). Upper 32 bits at offset 4 hold type in the top 3 bits and a per-type value (block index for DATA, dirent-hash for DENT) in the low 29 bits.

### UbifsWriter

Builds a minimal UBIFS image holding a flat list of small regular files plus the directory tree needed to reach them.

What this writer emits: a linear node stream — superblock, master, root-directory inode, per-file inode + dentry + zlib-compressed DATA node(s). Each node carries a fully valid 24-byte common header (magic, CRC-32 over the payload after the CRC field, sqnum, len, type, group=0) so the same linear scanner that drives `UbifsFileReader` can round-trip the image.

What's NOT emitted (out of scope — these require a full wandering-tree commit pipeline and a kernel-mountable image is a multi-week project): LPT (LEB Properties Tree), TNC (Tree Node Cache) index B+tree, commit-start / reference / orphan nodes, journal heads, padding/garbage-collection markers. A real mkfs.ubifs wires these together so the kernel can mount the result; our reader operates on a linear log scan and does not need them. Tests therefore validate self-round-trip, not kernel mount.

Compression: DATA nodes are zlib (DEFLATE) compressed when that shrinks the payload, otherwise stored. LZO/ZSTD are not emitted.

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

