# Apple IIgs GS/OS (2IMG) (`GsOs`)

Apple IIgs GS/OS 2IMG — 64-byte 2IMG header + ProDOS-ordered payload (HFS/DOS-3.3 payloads listed read-only).

> Generated from the implementation. Edit the doc comments on the descriptor,
> reader or writer rather than this file; a test regenerates it and fails on drift.

## At a glance

| | |
|---|---|
| Category | Archive |
| Family | Archive |
| Default extension | `.gsdos` |
| Recognised extensions | `.gsdos` |

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
| wipe free space | no | zero what no file holds |
| shrink | yes | reduce the volume to what it needs |
| optimise layout | no | re-lay the volume at a chosen geometry |
| report layout | no | say where every byte belongs |
| move blocks | no | relocate a run and repoint what names it |
| move metadata | no | relocate the volume's own structures |

### How it defragments

By rebuilding: every file is read out and a fresh volume is written in the
order the requested layout asks for. Correct, but it costs the whole payload.

## How a volume is laid out

### GsOsFormatDescriptor

Descriptor for Apple IIgs GS/OS 2IMG disk images. The 2IMG container wraps a ProDOS / HFS / DOS 3.3 volume with a 64-byte header — this descriptor parses the header, surfaces the inner volume, and (for ProDOS-ordered payloads) lets callers add/replace/remove files inside the embedded volume by delegating to `ProDosModifier`, which already shifts every block access past the 2IMG header.

Detection is by the .gsdos extension; the "2IMG" magic at offset 0 is owned by FileSystem.ProDos (.2mg routing) to avoid a detector first-match conflict.

References:

### GsOsReader

Reads Apple IIgs GS/OS disk images packaged in the 2IMG container (the canonical emulator format for IIgs disks). GS/OS is an extended ProDOS filesystem that adds Mac-HFS-style resource forks and longer filenames; volumes can be Extended ProDOS (version &gt;= 5), HFS, or DOS 3.3 — this reader handles the 2IMG header parse and surfaces the embedded volume as an opaque entry for delegation to a ProDOS / HFS reader downstream.

2IMG header layout (little-endian, 64 bytes): 0x00 char[4] "2IMG" 0x04 char[4] creator code (e.g. "CTKG"=Catakig, "ASIM"=ASIMOV2, "B2TR"=Bernie ][ The Rescue) 0x08 u16 header size (always 64) 0x0A u16 version 0x0C u32 image format (0=DOS 3.3 order, 1=ProDOS order, 2=NIB) 0x10 u32 flags (bit 0x80000000 = locked; low byte = volume number for DOS 3.3) 0x14 u32 data block count (ProDOS blocks) 0x18 u32 data offset (relative to file start) 0x1C u32 data length (bytes) 0x20 u32 comment offset 0x24 u32 comment length 0x28 u32 creator data offset 0x2C u32 creator data length 0x30..0x3F reserved

### GsOsWriter

Builds Apple IIgs GS/OS 2IMG disk images. The container is a 64-byte header (creator code, image format = ProDOS-ordered, data offset/length, flags) followed by the embedded ProDOS volume; this writer emits a ProDOS payload via `ProDosWriter` and prepends the 2IMG header so the result is recognised by GS/OS-aware emulators (CiderPress, ASIMOV2, Bernie ][ The Rescue, Catakig).

2IMG header layout (little-endian, 64 bytes) — see `GsOsReader` for the field-by-field breakdown. The flags word is left zero (unlocked, DOS-3.3 volume number = 0).

## Storage methods

- `stored` — Stored

## Further reading

- Apple II "Universal Disk Image" (2IMG) specification (Apple II emulation community, 1997) — defines the 64-byte header
- https://github.com/fadden/CiderPress2 — CiderPress II, maintained implementation with 2IMG format documentation
- http://fileformats.archiveteam.org/wiki/2IMG — Just Solve the File Format Problem wiki page

