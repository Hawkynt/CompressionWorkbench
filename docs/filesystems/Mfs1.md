# MFS-1 (Acorn Master File System v1) (`Mfs1`)

Acorn MFS-1 (BBC Master) — DFS-tier catalog walker with in-place R/W (Mfs1Writer + Mfs1InPlaceModifier).

> Generated from the implementation. Edit the doc comments on the descriptor,
> reader or writer rather than this file; a test regenerates it and fails on drift.

## At a glance

| | |
|---|---|
| Category | Archive |
| Family | Archive |
| Default extension | `.mfsd` |
| Recognised extensions | `.mfsd`, `.mfs` |

## Detection

| Bytes | At offset | Confidence |
|---|---|---|
| `00 80` | 0 | 0.20 |

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

By moving what is out of place, through `Mfs1BlockMover`.
A run is copied and whatever records its position is rewritten, so the cost is
the bytes that actually move rather than the whole volume.

| Property | Value | Meaning |
|---|---|---|
| Repoints runs independently | yes | whether a file in several pieces can be moved one piece at a time |
| Relinks a whole allocation | no | whether a scattered file's chain can be restated in one call |
| Holds runs outside the volume | yes | whether a full volume can be rearranged by lifting a run into memory |

## How a volume is laid out

### Mfs1FormatDescriptor

Read-only descriptor for Acorn MFS-1 (Master File System v1) disk images — the catalog-compatible evolution of Acorn DFS used on early Acorn / BBC Master systems. The on-disk catalog matches DFS (256-byte sectors, two-sector catalog at track 0 sectors 0-1, up to 31 entries with 7-char names + 1-char directory), so MFS-1 is parsed by walking those sectors directly. References:

Detection: weak — magic is the optional 0x00 0x80 boot pattern at offsets 0-1, low confidence (0.20). Stronger magic'd formats win. Real detection is extension-led (.mfs / .mfsd).

Write is supported via the DFS-tier catalog layout (sector 0 names + sector 1 metadata + contiguous data area from sector 2 onwards). Writer emits a self-consistent catalog with packed-high-bits encoding; the in-place modifier re-packs through the same writer so the outer sector count is preserved.

Distinct from FileSystem.Mfs, which targets the Macintosh File System with a strong 0xD2D7 magic.

### Mfs1Reader

Reads Acorn MFS-1 (Master File System v1) disk images. MFS-1 is the minor evolution of Acorn DFS used on early Acorn / BBC Master systems — the on-disk catalog layout matches DFS verbatim: The reader is intentionally forgiving — Acorn images frequently arrive with padding, optional boot sectors, or non-standard sizes. We parse the catalog best-effort; if the count byte or sector range looks invalid we surface no entries (the descriptor then falls back to the opaque FULL/metadata surface).

### Mfs1Writer

Builds Acorn MFS-1 (Master File System v1) disk images. MFS-1 inherits the DFS on-disk catalog: 256-byte sectors, a 2-sector catalog at track 0, up to 31 entries, files stored contiguously from sector 2 onwards.

Catalog layout (from `Mfs1Reader`): sector 0, bytes 0..7 — disk title (first 8 chars) sector 0, bytes 8..255 — up to 31 × 8-byte name entries (7 ASCII chars + 1 directory char, high bit = locked) sector 1, bytes 0..3 — disk title (last 4 chars) sector 1, byte 5 — entry count × 8 (i.e. byte offset of the next free slot) sector 1, bytes 8..255 — up to 31 × 8-byte metadata entries: load_lo(2) + exec_lo(2) + length_lo(2) + packed_high_bits(1) + start_sector_lo(1) packed_high_bits bits: 0-1 start_sector_hi, 2-3 load_hi, 4-5 length_hi, 6-7 exec_hi.

Files are stored contiguously from sector 2 onwards in catalog-insertion order; the catalog itself is sorted by descending start-sector per DFS convention (the most-recently-added file appears first). Total image size defaults to 80 tracks × 10 sectors × 256 bytes = 200 KB (a Master 80-track SSD image); pass totalSectors to `Build` to choose a different geometry.

### Mfs1ExtentMap

Reports where an MFS-1 disk's bytes are: the two catalog sectors, each file's run of sectors under its name, and the rest as free.

The disk had no layout to report at all, which left every layout-aware verb with nothing to work from. Acorn's catalog is two sectors of fixed slots and each slot records where its file starts, so both questions — which sectors are taken and by whom — are answered by reading it.

## Parameters

| Key | Kind | Default | Allowed | Meaning |
|---|---|---|---|---|
| `VolumeLabel` | String | `` | any | Volume name shown by file managers (max 12 chars). |

## Storage methods

- `stored` — Stored

## Further reading

- https://beebwiki.mdfs.net/Acorn_DFS_disc_format — the DFS catalog layout MFS-1 shares (two-sector catalog at track 0)
- https://en.wikipedia.org/wiki/Disc_Filing_System — Wikipedia article on the DFS family
- Acorn "Disc Filing System User Guide" (vendor manual)

