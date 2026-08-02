# NIB (Commodore nibble dump) (`Nib`)

Commodore 1541 raw nibble dump (nibtools / ZoomFloppy)

> Generated from the implementation. Edit the doc comments on the descriptor,
> reader or writer rather than this file; a test regenerates it and fails on drift.

## At a glance

| | |
|---|---|
| Category | Archive |
| Family | Archive |
| Default extension | `.nib` |
| Recognised extensions | `.nib` |

## Detection

No byte signature: this format is recognised by its extension and by the
reader accepting the volume's own structures.

## Verbs

| Verb | Offered | What it does |
|---|---|---|
| list / extract | yes | read the volume and copy files out of it |
| create | no | write a fresh volume holding the given files |
| add / remove | no | change a volume in place |
| defragment | no | lay the volume out again |
| wipe free space | no | zero what no file holds |
| shrink | no | reduce the volume to what it needs |
| optimise layout | no | re-lay the volume at a chosen geometry |
| report layout | no | say where every byte belongs |
| move blocks | no | relocate a run and repoint what names it |
| move metadata | no | relocate the volume's own structures |

### How it defragments

It does not.

## How a volume is laid out

### NibFormatDescriptor

Commodore NIB raw nibble dump (nibtools / ZoomFloppy). No magic header — detected by file extension only; the typical dump is exactly 84 × 8192 bytes. References:

### CbmNibbleReader

Reader for Commodore 1541/1571 nibble dumps — both the raw .nib format (used by nibtools and ZoomFloppy) and the .g64 GCR track container produced by emulators like VICE. Converting GCR back to a cleanly sectored D64 is outside scope for this sweep; this reader detects the format variant and surfaces each track as a raw byte buffer for downstream tools to consume.

NIB format: a flat dump of 84 half-tracks × 0x2000 (8192) bytes each — the raw 1541 read-head stream including sync marks and jitter. There is no magic header; detection is extension-only, and the typical file size is exactly 84 × 8192 = 688 128 bytes.

G64 format: per VICE spec, a 12-byte signature "GCR-1541\0\x00\x00\xA2\xA2" (byte 8 = version, byte 9 = track count, bytes 10-11 = max track-data size in bytes little-endian), followed by an offset table of track_count u32 LE entries (0 = empty track), an equal-length u32 LE speed-zone table, and the raw track data blocks. Each track block starts with a u16 LE length followed by the GCR bytes.

### CbmNibbleWriter

From-scratch writer for the Commodore nibble container the `CbmNibbleReader` consumes. The Commodore 1541 filesystem is flat — files live in the single directory on track 18 with a BAM — so the writer first builds a standard sectored D64 image (reusing `D64Writer` for the BAM, directory and linked sector chains) and then GCR-encodes every track into the VICE `.g64` wire format, framing each sector with sync marks, a header block and a data block exactly as a real 1541 lays them down on disk.

The reader surfaces each G64 track as an opaque GCR byte buffer; it does not decode GCR. `DecodeToD64` performs the inverse transform so a caller can recover the sectored image (and thus the directory and file contents) from the tracks the reader hands back.

## Storage methods

- `stored` — Stored

## Further reading

- nibtools (Pete Rittwage's C64 Disk Preservation Project) — the tool that defines and produces the de-facto NIB dump layout
- http://unusedino.de/ec64/technical/formats/g64.html — Peter Schepers' GCR track documentation (shared with G64)

