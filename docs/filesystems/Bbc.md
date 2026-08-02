# BBC DFS (`Bbc`)

BBC Micro Acorn DFS floppy disk image

> Generated from the implementation. Edit the doc comments on the descriptor,
> reader or writer rather than this file; a test regenerates it and fails on drift.

## At a glance

| | |
|---|---|
| Category | Archive |
| Family | Archive |
| Default extension | `.ssd` |
| Recognised extensions | `.ssd`, `.dsd` |

## Detection

No byte signature: this format is recognised by its extension and by the
reader accepting the volume's own structures.

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

By moving what is out of place, through `BbcBlockMover`.
A run is copied and whatever records its position is rewritten, so the cost is
the bytes that actually move rather than the whole volume.

| Property | Value | Meaning |
|---|---|---|
| Repoints runs independently | no | whether a file in several pieces can be moved one piece at a time |
| Relinks a whole allocation | no | whether a scattered file's chain can be restated in one call |
| Holds runs outside the volume | no | whether a full volume can be rearranged by lifting a run into memory |

## How a volume is laid out

### BbcFormatDescriptor

References:

### BbcReader

Reader for BBC Micro Acorn DFS `.ssd` (single-sided) and `.dsd` (double-sided interleaved) disk images.

Layout: tracks x 10 sectors x 256 bytes. Catalog is on track 0 sectors 0-1. Sector 0: disk title (first 8 chars) + up to 31 eight-byte name entries (7-char filename + 1-char directory; high bit of directory byte = locked). Sector 1: last 4 title chars, (count x 8) in byte 5, total-sectors bits in bytes 6-7, plus 31 eight-byte metadata entries (load/exec/length/start-sector, high bits packed into byte 6).

### BbcWriter

Builds a fresh BBC Micro Acorn DFS `.ssd` single-sided disk image from scratch (WORM).

Layout: N tracks x 10 sectors x 256 bytes. Catalog occupies track 0 sectors 0 (names + directory chars) and 1 (load/exec/length/start metadata). Each sector holds up to 31 8-byte entries. File data lives starting at track 0 sector 2.

Writer emits a 40-track SSD (100 000 bytes) by default, matching the historical BBC-B DFS floppy. Filenames are padded/truncated to 7 chars ASCII, directory character defaults to '$' (root).

### BbcExtentMap

Walks a BBC Micro Acorn DFS image (.ssd / .dsd, 256-byte sectors, 10 sectors/track) and yields its actual on-disk byte layout — sectors 0+1 of each side as the catalog (metadata), every per-file (start_sector, length) extent as a single contiguous run, and the unallocated sectors as Free.

## Parameters

| Key | Kind | Default | Allowed | Meaning |
|---|---|---|---|---|
| `BootOption` | Enum | `None` | `None`, `LOAD`, `RUN`, `EXEC` | What SHIFT-BREAK does with $.!BOOT: None = nothing; LOAD = *LOAD $.!BOOT; RUN = *RUN $.!BOOT; EXEC = *EXEC $.!BOOT. Stored at catalog sector 1 byte 6 bits 4-5. |
| `VolumeLabel` | String | `` | any | Volume name shown by file managers (max 12 chars). |

## Storage methods

- `stored` — Stored

## Further reading

- https://beebwiki.mdfs.net/Acorn_DFS_disc_format — BeebWiki's Acorn DFS disc format page, the de-facto on-disk reference (catalog sectors, boot option)
- Acorn "Disc Filing System User Guide" (Acorn Computers) — original vendor documentation
- https://en.wikipedia.org/wiki/Disc_Filing_System — Wikipedia overview

