# DriveSpace 3 CVF (`DriveSpace3`)

Microsoft DriveSpace 3 CVF (Win95 Plus! Pack 1995) — defrag/wipe/modify/extent-map/block-mover parity with DoubleSpace via shared MDBPB infrastructure; per-cluster MS LZH with full effort tiers (greedy / lazy / iterated) and stored-run fallback at every tier.

> Generated from the implementation. Edit the doc comments on the descriptor,
> reader or writer rather than this file; a test regenerates it and fails on drift.

## At a glance

| | |
|---|---|
| Category | Archive |
| Family | Archive |
| Default extension | `.cvf` |
| Recognised extensions | none |

## Detection

| Bytes | At offset | Confidence |
|---|---|---|
| `4D 53 5F 44 53 50 33` | 3 | 0.95 |

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

By moving what is out of place, through `DriveSpace3FormatDescriptor`.
A run is copied and whatever records its position is rewritten, so the cost is
the bytes that actually move rather than the whole volume.

| Property | Value | Meaning |
|---|---|---|
| Repoints runs independently | no | whether a file in several pieces can be moved one piece at a time |
| Relinks a whole allocation | no | whether a scattered file's chain can be restated in one call |
| Holds runs outside the volume | no | whether a full volume can be rearranged by lifting a run into memory |

## How a volume is laid out

### DriveSpace3FormatDescriptor

Descriptor for Microsoft DriveSpace 3 CVF (Windows 95 Plus! Pack, 1995). Distinguished from DoubleSpace/DriveSpace 2 by the `MS_DSP3` MDBPB signature at file offset 3 and the `DVR3` CvfSignature at offset 36. The compression algorithm changed from DS LZ77 (DOS 6.x) to MS LZH (LZ77 + canonical Huffman).

Read/write/modify/defrag are delegated to the shared DoubleSpace infrastructure (`DoubleSpaceWriter` routed through `DriveSpace3`, `DoubleSpaceReader` with MS LZH dispatch, `DoubleSpaceExtentMap`, `DoubleSpaceBlockMover`) — the on-disk MDBPB+MDFAT+BitFAT layout is byte-compatible across the whole CVF family; only the OEM bytes and inner-cluster codec differ. This brings DriveSpace 3 to full parity with DoubleSpace/DriveSpace 6.22 for defrag, wipe-empty, modify, extent map and block mover.

Shares the .cvf extension with DoubleSpace; FormatDetector disambiguates by magic.

References:

### DriveSpace3Reader

Reads DriveSpace 3 (Microsoft Plus! Pack for Windows 95, 1995) CVF images. The on-disk layout is the DOS 6.x DBLSPACE/DRVSPACE MDBPB + MDFAT + BitFAT + DATA chain — only the OEM name (`MS_DSP3`), CvfSignature (`DVR3`), and per-cluster compression algorithm (MS LZH instead of DS LZ77) change.

Compressed runs (MDFAT flag = 2) are decoded through `MsLzhBlockCodec`; stored runs (flag = 1) are returned verbatim. The inner FAT16 chain is walked starting from each entry's first cluster, with the MDFAT indirection resolving every cluster to its physical run in the DATA region. Clusters without a valid MDFAT mapping fall back to the inner-data mirror, mirroring the strategy used by FileSystem.DoubleSpace.DoubleSpaceReader.

### DriveSpace3Writer

Builds a spec-compliant Microsoft DriveSpace 3 CVF (Windows 95 Plus! Pack, 1995). On-disk layout mirrors the DOS 6.x DBLSPACE/DRVSPACE convention: MDBPB → inner FAT16 → inner root dir → MDFAT → BitFAT → DATA region. The only differences from `FileSystem.DoubleSpace.DoubleSpaceWriter` are:

This writer emits either stored runs (MDFAT flag = 1) or MS LZH-compressed runs (flag = 2), depending on whether the codec shrinks the cluster. The reader follows the inner FAT chain through the MDFAT indirection back to each compressed run, exactly as DoubleSpace does. Stage 2 self round-trip is the gating requirement; bit-stream parity with Microsoft's reference driver (DRVSPACE.BIN) is a future external-tool conformance gate.

## Parameters

| Key | Kind | Default | Allowed | Meaning |
|---|---|---|---|---|
| `Compatibility` | Enum | `Extended` | `Genuine`, `Extended` | Genuine — the real Windows 95 DriveSpace 3 layout (MSDBL6.0 container, 32 KB clusters, version flag 3, 5-byte MDFAT, inner FAT16). Mounted and read byte-exact by the independent dmsdos driver (and the real Win95 Plus!/OSR2 DriveSpace 3 driver). Clusters are STORED (uncompressed); single flat root directory; up to ~511 clusters. Choose this for interoperability with real DriveSpace tooling — the MS LZH compression methods do not apply here. Extended — CompressionWorkbench's feature layout (MS_DSP3/DVR3 header). Adds per-cluster MS LZH compression, long filenames, in-place add/remove, defrag and block-mover support, but is readable ONLY by CompressionWorkbench — NOT by the genuine DriveSpace driver or dmsdos. |
| `ForceCompress` | Boolean | `false` | any | Keep the compressed form even when it does not shrink a cluster (overrides the per-cluster auto-best stored fallback). |
| `Level` | Integer | `2` | any | Codec search effort (1 = fast, higher = better ratio, slower). |
| `Method` | Enum | `Auto` | `Stored`, `JM`, `SQ`, `Auto` | Per-cluster compression for the Genuine layout. Stored = none. JM = DriveSpace 3 'JM-0-x' LZ (Normal/High). SQ = DriveSpace 3 'SQ-0-0' (Ultra; DEFLATE). Both are read by the real driver and dmsdos. Auto = per cluster keep the smallest of all codecs, falling back to stored. |
| `Timestamp` | String | `` | any | Optional ISO-8601 date/time (e.g. 1996-08-24) stamped on every file's FAT directory entry. Blank leaves the date/time unset (Genuine layout only). |
| `VolumeLabel` | String | `` | any | Optional 11-char inner-volume label written to the root directory (Genuine layout only). |

## Storage methods

- `stored` — Stored (no compression)
- `ms-lzh` — MS LZH
- `ms-lzh+` — MS LZH (lazy matching, slower better ratio)
- `ms-lzh++` — MS LZH (iterated parsing, best ratio)

## Further reading

- https://github.com/sandsmark/dmsdos — dmsdos, the GPL Linux CVF driver whose source + doc/dmsdos.doc are the de-facto DriveSpace 3 on-disk specification (5-byte MDFAT, MS LZH codecs)
- Microsoft Plus! for Windows 95 documentation (DriveSpace 3) — original vendor description
- https://en.wikipedia.org/wiki/DriveSpace — Wikipedia overview of the CVF family

