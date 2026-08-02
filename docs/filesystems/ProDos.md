# ProDOS (`ProDos`)

Apple II / Apple IIgs ProDOS filesystem image

> Generated from the implementation. Edit the doc comments on the descriptor,
> reader or writer rather than this file; a test regenerates it and fails on drift.

## At a glance

| | |
|---|---|
| Category | Archive |
| Family | Archive |
| Default extension | `.po` |
| Recognised extensions | `.po`, `.2mg` |

## Detection

| Bytes | At offset | Confidence |
|---|---|---|
| `32 49 4D 47` | 0 | 0.95 |
| `00 00 03 00 F0` | 1024 | 0.85 |

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
| move blocks | yes | relocate a run and repoint what names it |
| move metadata | no | relocate the volume's own structures |

### How it defragments

By moving what is out of place, through `ProDosBlockMover`.
A run is copied and whatever records its position is rewritten, so the cost is
the bytes that actually move rather than the whole volume.

| Property | Value | Meaning |
|---|---|---|
| Repoints runs independently | no | whether a file in several pieces can be moved one piece at a time |
| Relinks a whole allocation | no | whether a scattered file's chain can be restated in one call |
| Holds runs outside the volume | no | whether a full volume can be rearranged by lifting a run into memory |

## How a volume is laid out

### ProDosFormatDescriptor

Descriptor for Apple II ProDOS volume images (140 KB / 800 KB) — volume directory + bitmap layout with seedling/sapling/tree file storage tiers. References:

### ProDosReader

Reader for Apple ProDOS `.po` (block-ordered) and `.2mg` images.

ProDOS is block-based (512-byte blocks). The volume directory starts at block 2 and chains through adjacent blocks via a prev/next pointer pair at the start of each directory block. Each directory block holds thirteen 39-byte entries. File storage tiers: seedling (1 block, up to 512 bytes), sapling (index block of block pointers, up to 128 KB), tree (master index -&gt; index blocks -&gt; data).

### ProDosWriter

Builds a fresh Apple ProDOS block-ordered disk image (`.po`) from scratch (WORM).

Layout: 512-byte blocks. Canonical sizes are 280 blocks (143 360 B — 5.25" floppy) and 1 600 blocks (819 200 B — 800 KB Mac-format 3.5" floppy). The volume directory starts at block 2 and chains through blocks 2..5 (4 blocks total in this writer). Each directory block holds thirteen 39-byte entries at offset 4.

This writer emits a hierarchical volume directory: files whose name contains '/' separators are placed inside real ProDOS subdirectories (storage type 0xD pointing at a 0xE subdirectory header) rather than flattened into the volume root. File data is stored with seedling / sapling / tree storage types as appropriate.

Subdirectories grow their block chain on demand: each subdirectory is allocated ceil((childCount + 1) / 13) blocks, chained through the prev/next pointer pair at each block's start, so a single subdirectory can hold hundreds of children (limited only by the volume's free-block count). The volume (root) directory keeps the canonical fixed 4-block chain at blocks 2..5 — a genuine ProDOS layout constraint — and therefore still holds at most 51 children; place large fan-outs in a subdirectory rather than the volume root.

### ProDosExtentMap

Walks an Apple ProDOS image (.po / .2mg, 512-byte blocks) and yields the actual on-disk byte layout — boot (blocks 0-1), volume directory chain, volume bitmap, and per-file storage as Used (data blocks + index/master-index blocks attributed to the file). Storage tiers 1 (seedling), 2 (sapling), 3 (tree), and 0xD (subdirectory) are all walked.

## Parameters

| Key | Kind | Default | Allowed | Meaning |
|---|---|---|---|---|
| `ImageSize` | Enum | `Auto (fit to files)` | `Auto (fit to files)`, `140 KB (5.25")`, `800 KB (3.5")` | ProDOS volume size. Auto uses 140 KB and promotes to 800 KB when the files don't fit. |
| `VolumeLabel` | String | `` | any | ProDOS volume name (max 15 chars; letters, digits and periods; must start with a letter). |

## Storage methods

- `stored` — Stored

## Further reading

- https://prodos8.com/docs/techref/ — ProDOS 8 Technical Reference Manual — volume/directory/storage-tier spec
- https://github.com/fadden/CiderPress2 — CiderPress II — maintained tooling for ProDOS volumes
- https://en.wikipedia.org/wiki/Apple_ProDOS — Wikipedia article

