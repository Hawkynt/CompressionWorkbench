# Stacker CVF (`Stacker`)

Stacker STACVOL (Stac Electronics, MS-DOS) — banner + Stacker Control Block parsed, inner FAT12 directory walked, STORED and Stac-LZS clusters read/written. Choose the 'Genuine' layout for byte-exact compatibility with the real Stacker driver / dmsdos, or 'Extended' for CompressionWorkbench-only LZS compression.

> Generated from the implementation. Edit the doc comments on the descriptor,
> reader or writer rather than this file; a test regenerates it and fails on drift.

## At a glance

| | |
|---|---|
| Category | Archive |
| Family | Archive |
| Default extension | `.sta` |
| Recognised extensions | `.sta`, `.stk` |

## Detection

| Bytes | At offset | Confidence |
|---|---|---|
| `53 54 41 43 4B 45 52` | 0 | 0.90 |

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
| report layout | no | say where every byte belongs |
| move blocks | no | relocate a run and repoint what names it |
| move metadata | no | relocate the volume's own structures |

### How it defragments

By rebuilding: every file is read out and a fresh volume is written in the
order the requested layout asks for. Correct, but it costs the whole payload.

## How a volume is laid out

### StackerFormatDescriptor

Descriptor for the Stacker STACVOL compressed volume (Stac Electronics, MS-DOS) — the historical predecessor of Microsoft's DoubleSpace (DOS 6.0) and DriveSpace (DOS 6.22 / Win 95). A STACVOL wraps a compressed inner FAT12 volume behind an ASCII banner and a Stacker Control Block (BPB); clusters are STORED verbatim or Stac-LZS compressed (RFC 1967/2395). Detection is by the ASCII "STACKER" banner at file offset 0. References:

### StackerReader

Reads a Stacker STACVOL compressed volume (Stac Electronics, MS-DOS 1990-1993, the historical predecessor of Microsoft DoubleSpace/DriveSpace). A STACVOL is a host file wrapping a compressed inner FAT12 volume.

Physical layout (512-byte sectors, little-endian) — see FORMAT-NOTES.md:

The reader parses the genuine banner + SCB of real Stacker volumes and walks the inner FAT directory. Cluster payload is resolved through the explicit STORED/LZS sector map that `StackerWriter` emits; genuine empty volumes (no allocated clusters) list only the inner volume label.

### StackerWriter

Emits a Stacker STACVOL that `StackerReader` round-trips byte-exact. The container reproduces the genuine banner + Stacker Control Block (BPB) and a real inner FAT12 image; file payload is laid out as STORED or Stac-LZS clusters tracked by the explicit STKMAP01 sector map documented in FORMAT-NOTES.md. Incompressible data is stored verbatim.

## Parameters

| Key | Kind | Default | Allowed | Meaning |
|---|---|---|---|---|
| `Compatibility` | Enum | `Extended` | `Genuine`, `Extended` | Genuine — the real Stac Electronics STACVOL layout (obfuscated superblock + emulated boot block + interleaved AMAP). Mounted and read byte-exact by the independent dmsdos driver (and by the original Stacker 3.x/4.x DOS driver). Clusters are STORED (uncompressed); single flat root directory; up to ~511 clusters. Use this for interoperability with real Stacker tooling. Extended — CompressionWorkbench's own layout (STKMAP01 sector-map trailer) with Stac-LZS per-cluster compression. Smaller images, but readable ONLY by CompressionWorkbench — NOT by the genuine Stacker driver or dmsdos. |
| `ForceCompress` | Boolean | `false` | any | Keep the compressed form even when it does not shrink a cluster. |
| `Level` | Integer | `2` | any | Codec search effort (1 = fast, higher = better ratio, slower). |
| `Method` | Enum | `Auto` | `Stored`, `DS`, `SD4`, `Auto` | Per-cluster compression for the Genuine layout. Stored = none. DS = the 'DS' LZ stream the Stacker driver (and dmsdos) decode. SD4 = Stacker 4 native Huffman codec (header 0x0081). Auto = per cluster keep the smaller of DS/SD4, else stored. |
| `Timestamp` | String | `` | any | Optional ISO-8601 date/time (e.g. 1994-02-01) stamped on every file's FAT directory entry. Blank leaves the date/time unset (Genuine layout only). |
| `Version` | Enum | `3` | `3`, `4` | Stacker format version stamped into the volume superblock. 3 = Stacker 3.x (MS-DOS 6 era); 4 = Stacker 4.x. The dmsdos driver reads both. Applies to the Genuine layout only. |
| `VolumeLabel` | String | `` | any | Optional 11-char inner-volume label written to the root directory (Genuine layout only). |

## Storage methods

- `stacker-lzs` — Stacker LZS

## Further reading

- https://github.com/sandsmark/dmsdos — dmsdos driver — the de-facto public documentation of the STACVOL layout and cluster compression
- https://www.rfc-editor.org/rfc/rfc1967 — LZS-DCP (the Stac LZS algorithm)
- https://www.rfc-editor.org/rfc/rfc2395 — LZS in IPsec — independent description of the same algorithm
- https://en.wikipedia.org/wiki/Stac_Electronics — Wikipedia article

