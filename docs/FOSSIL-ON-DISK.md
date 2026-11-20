# Fossil on disk

Fossil is the Plan 9 file server (Quinlan, McKie and Cox, 2002). It keeps the active file
system and its local snapshots in one partition of copy-on-write blocks, and can archive
snapshots to Venti. This page describes what `FileSystem.Fossil` reads.

**Sources (rung 1).** The fossil sources in plan9port
(<https://github.com/9fans/plan9port>, `src/cmd/fossil`, revision `1f13969`) are under the
MIT licence since the Plan 9 Foundation relicensed Plan 9 in 2021. The reader converts
their unpack routines (`pack.c`, `vac.c`) and their block walk (`source.c`, `file.c`).
Notice: `THIRD-PARTY-NOTICE.Fossil.txt` beside the code.

All integers are big-endian.

## Header (byte 131072, 512 bytes)

| Offset | Size | Field |
| --- | --- | --- |
| 0 | 4 | magic `0x3776ae89` |
| 4 | 2 | version, 1 |
| 6 | 2 | block size |
| 8 | 4 | block number of the superblock |
| 12 | 4 | first block of the label partition |
| 16 | 4 | first block of the data partition (the end of the labels) |
| 20 | 4 | end of the data partition |

Data block *a* lives at byte `(data + a) × blockSize`.

## Superblock (512 bytes)

`magic 0x2340a3b1` (4), version 1 (2), epochLow (4), epochHigh (4), next qid (8),
**active** (4, the block holding the active root), next (4), current (4), the last archived
score (20), and the name (128).

## Entries ("sources") — 40 bytes

| Offset | Size | Field |
| --- | --- | --- |
| 0 | 4 | gen |
| 4 | 2 | psize: pointer block size (holds psize/20 scores) |
| 6 | 2 | dsize: data block size |
| 8 | 1 | flags: 0x01 active, 0x02 directory (the source holds entries), bits 2–4 depth, 0x20 local, 0x80 no-archive |
| 14 | 6 | size in bytes (48-bit) |
| 20 | 20 | score when not local |
| 27 / 28 / 32 / 36 | 1 / 4 / 4 / 4 | local entries only: archive flag, snapshot epoch, tag, block address |

A score with 16 leading zero bytes is a local block number in its last 4 bytes.
`DA39A3EE…0709`, the SHA-1 of nothing, is an empty block. Any other score is a Venti block.

**Block *bn* of a source:** the entry names its top block. With `np = psize/20`, write *bn*
in base *np*. Starting from the top block, the depth−1 … 0 digits pick the score in each
pointer block. The leaf is a data block, `dsize` bytes, cut to the source size.

## Directory tree

The block at `active` holds one entry: the root source. Its entries 0, 1 and 2 are the
root directory's entry stream, its meta stream, and a meta stream naming the root itself.

A meta stream is a sequence of `dsize` MetaBlocks. A MetaBlock starts with magic
`0x5656fc7a` (or one less, for the old sort order), then size, free, maxindex and nindex
(2 bytes each). Next comes an index of (offset, length) pairs, then the DirEntries.

A DirEntry has magic `0x1c4d9072` (4), version 7–9 (2), the element name (2-byte length +
bytes), entry (4), and gen, mentry and mgen (4 each, from version 9). Then come the qid
(8), a 20-byte replacement score in version 7 only, and uid, gid and mid (strings). The
mtime, mcount, ctime, atime and mode (4 each) follow, then optional sections.

A child is entry `entry` of its parent's entry stream, and its `gen` must match. A directory
also has a meta stream: entry `mentry`, generation `mgen`. Mode bit `1<<15` marks a
directory, and `1<<20` a snapshot.

The Fossil root contains `active` (the live tree) and `archive`. After `snap` it also
contains `snapshot/YYYY/MMDD/HHMM`, whose sources carry the snapshot epoch.

## What the implementation does with it

It reads the whole local tree, files and directories with modes, owners and mtimes,
snapshots included. Entries must be active and of the right generation, every block must
lie in the data partition, and nesting is bounded. A Venti score is refused, because there
is no Venti here. `FossilWriter` writes fresh volumes (see *Writing*).

Checked against a volume formatted by `flfmt` from plan9port and filled by plan9port's
`fossil` server over 9P. It holds a two-level pointer tree, nested and empty directories,
and a snapshot taken before a rewrite. It reads back byte-identical to what that server
returned. `flchk` was not usable as an oracle: without Venti it reports errors even on a
freshly formatted volume.

## Writing

`FossilWriter` lays a volume out the way `flfmt` formats it and the `fossil` server adds files
to it:

* **Layout.** The 128 KiB gap, the header, then the partitions `flfmt` cuts from the file size:
  the superblock at `(128 KiB + 2 blocks) / block size`, the label blocks after it, and the
  data blocks. The data partition keeps 64 blocks spare for the server's own first writes.
* **Labels.** Every data block written gets a label: state allocated, epoch 1, not closed, and
  the type and tag of its source. The type is `BtData` or `BtDir`, plus the pointer level.
  Every other label stays zero (free).
* **Superblock.** Epochs 1 and 1, the next qid, the root block, no next or current block, the
  empty score as the last archive, and the name.
* **Root.** A `RootTag` block holds one entry, a directory source of three entries:
  - the root directory's entries;
  - its meta source;
  - a meta block naming the root itself (`root`, entry 0, meta entry 1).

  The root directory holds `active`, `archive` and `snapshot`. The given tree is `active`.
* **Directories.** Each child takes an entry in its parent's entry source, a directory two
  (entries, then meta). Every entry source holds `blockSize / 40` entries per block, and its
  size counts whole blocks plus 40 bytes per entry of the last one. The meta source is meta
  blocks of at most `blockSize / 100` DirEntries (version 9), each block's index sorted by name.
* **Files.** A file is a source of data blocks (the last zero-filled) under as many pointer
  levels as it needs: `psize / 20` scores per pointer block, with unused slots holding the
  empty score. An empty file or directory is a source with no blocks.
* **Fields.** Generations are 0; qids count from 1 (the root) in creation order. Owner, group
  and last modifier are the input's owner (`adm` by default). The modification time is also the
  change and access time.

Checked by plan9port's `fossil` server (`Compression.Tests/Fossil/ReferenceVectors/serve-and-read.sh`).
It serves our volumes, at 4 and 8 KiB blocks, with every file's size, contents and mode as
written. It then creates and writes a file of its own, and we read the volume it leaves.
