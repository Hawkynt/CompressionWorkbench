# OpenVMS Files-11 (`OpenVms`)

DEC/VMS Files-11 (ODS-2) — clean-room writer + reader + in-place Add/Remove/Replace modifier sharing the workbench-layout geometry (BITMAP.SYS, INDEXF.SYS, 000000.DIR at fixed LBNs). Honest scope: emitted volumes are not OpenVMS-mountable — home-block HM2$W_CHECKSUM1/CHECKSUM2, FH FILECHAR/RECATTR bundles, and ODS-2 variable-length directory records remain deferred.

> Generated from the implementation. Edit the doc comments on the descriptor,
> reader or writer rather than this file; a test regenerates it and fails on drift.

## At a glance

| | |
|---|---|
| Category | Archive |
| Family | Archive |
| Default extension | `.ods2` |
| Recognised extensions | `.ods2`, `.ods5`, `.vmsdisk` |

## Detection

| Bytes | At offset | Confidence |
|---|---|---|
| `44 45 43 46 49 4C 45 31 31 41 20` | 1000 | 0.85 |
| `44 45 43 46 49 4C 45 31 31 42 20` | 1000 | 0.85 |

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

By moving what is out of place, through `OpenVmsBlockMover`.
A run is copied and whatever records its position is rewritten, so the cost is
the bytes that actually move rather than the whole volume.

| Property | Value | Meaning |
|---|---|---|
| Repoints runs independently | no | whether a file in several pieces can be moved one piece at a time |
| Relinks a whole allocation | no | whether a scattered file's chain can be restated in one call |
| Holds runs outside the volume | no | whether a full volume can be rearranged by lifting a run into memory |

## How a volume is laid out

### OpenVmsFormatDescriptor

Read/write descriptor for OpenVMS Files-11 (ODS-2) volume images. Backed by a clean-room writer / reader / in-place modifier trio that shares the geometry pinned at `OpenVmsLayout`. The descriptor advertises:

Honest scope. The emitted volume is not OpenVMS-mountable — the home block's HM2$W_CHECKSUM1/CHECKSUM2 surfaces, the FH FILECHAR and RECATTR bundles, the ODS-2 variable-length directory record format, and the per-file revision-history fields are out of scope. What it IS is a layout the workbench's own writer, reader and in-place modifier can round-trip end-to-end through Add / Remove / Replace.

References:

### OpenVmsReader

Walks a workbench-layout OpenVMS Files-11 ODS-2 volume and surfaces the user files held in 000000.DIR. Confirms the volume by checking the "workbench-layout" layout marker at byte 132 of the home block — when the marker is absent the reader returns no entries (the descriptor's generic header-surface path takes over).

For each file the reader produces an `Entry` bundle containing the File-ID, name, logical size, and the in-memory file bytes (assembled by walking the File Header's retrieval pointers).

### OpenVmsWriter

Emits a fresh OpenVMS Files-11 (ODS-2) volume to the `OpenVmsLayout` geometry. The resulting image carries:

Honest scope: this volume is NOT OpenVMS-mountable — the FH ident-area metadata, the FILECHAR / RECATTR fields, and the home-block HM2$W_CHECKSUM1/CHECKSUM2 surfaces are emitted as zeros. What it IS: a layout our reader and in-place modifier can round-trip end-to-end.

### OpenVmsLayout

Volume geometry for the CompressionWorkbench OpenVMS Files-11 ODS-2 writer. These constants pin every LBN that the writer, reader, and in-place modifier agree on. The numbers are NOT load-bearing on a real OpenVMS instance — VMS mountability is out of scope (per the descriptor's honest-scope notice) — but the writer, reader and in-place modifier MUST agree exactly, otherwise an Add or Remove will desync the allocation bitmap from the file headers.

Layout (512-byte LBNs):

## Parameters

| Key | Kind | Default | Allowed | Meaning |
|---|---|---|---|---|
| `VolumeLabel` | String | `` | any | Volume name shown by file managers (max 12 chars). |

## Storage methods

- `stored` — Stored

## Further reading

- `CanList` + `CanExtract` — driven by `OpenVmsReader` walking 000000.DIR.
- `CanCreate` — driven by `OpenVmsWriter`. The fresh volume carries a real ODS-2 home block at LBN 1 plus a workbench-layout layout marker at byte 132 of the home block.
- `CanModify` — driven by `OpenVmsInPlaceModifier`. Add / Remove / Replace touch only the BITMAP.SYS sector, the file's INDEXF.SYS slot, the directory block, and the affected data LBNs.
- DEC "Files-11 On-Disk Structure Specification" — the canonical ODS-2 spec (archived at Bitsavers)
- Kirby McCoy, "VMS File System Internals" (Digital Press, 1990)
- https://en.wikipedia.org/wiki/Files-11 — Wikipedia article

