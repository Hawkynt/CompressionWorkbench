# Transactional FAT (TFAT) (`TFat`)

Windows CE / Embedded Compact Transactional FAT (dual-FAT atomic commit)

> Generated from the implementation. Edit the doc comments on the descriptor,
> reader or writer rather than this file; a test regenerates it and fails on drift.

## At a glance

| | |
|---|---|
| Category | Archive |
| Family | Archive |
| Default extension | `.tfat` |
| Recognised extensions | `.tfat` |

## Detection

| Bytes | At offset | Confidence |
|---|---|---|
| `54 46 41 54` | 54 | 0.92 |
| `54 46 41 54` | 82 | 0.92 |

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

By rebuilding: every file is read out and a fresh volume is written in the
order the requested layout asks for. Correct, but it costs the whole payload.

## How a volume is laid out

### TFatFormatDescriptor

Transactional FAT (TFAT) — Microsoft Windows CE / Windows Embedded Compact variant of FAT12/16/32 that uses dual FAT copies as a two-phase commit log. The on-disk layout is identical to standard FAT; TFAT differs only in (a) detection markers in the BPB and (b) the runtime protocol that alternates which FAT is "active" on each transaction.

This descriptor delivers read, WORM-create and true in-place transactional update support via the alternating-FAT commit protocol implemented in `TFatModifier`. Each Add or Remove is a single transaction: writes go to the inactive FAT, then a single 4-byte big-endian sequence-number write at the end of that FAT region commits the transaction. A crash before the sequence write leaves the old FAT (still active) untouched and the transaction is invisible.

Defragment is implemented via `DefragRebuilder` over `TFatReader` + `TFatWriter`: the image is rebuilt from scratch, then re-stamped with TFAT markers so both FAT copies stay in lock-step. This is intentionally non-transactional (it rewrites the whole image, not a single FAT) because defrag is an offline operation.

Limitation: FAT12/16 with the fixed-area root directory is fully supported for in-place modification. FAT32 root-cluster updates are not supported — CE TFAT usage typically pins the root cluster, and extending the transactional protocol to cover variable-size root directories would require integrating dir-cluster allocation into the commit point. WORM-create still works for FAT32.

Spec sources: TFAT marker layout from public Microsoft Windows CE / Windows Embedded Compact documentation on the FAT transactional protocol, supplemented by forensic-literature summaries. The runtime protocol itself is documented in Microsoft's WinCE TFAT design notes.

References:

### TFatReader

Reads Transactional FAT (TFAT) filesystem images used by Windows CE / Windows Embedded Compact. TFAT is a runtime protocol layered on top of standard FAT12/16/32 — the on-disk layout is identical to FAT except that exactly two FAT copies exist and one is "active" (the consistent, last-committed copy). Power-fail safety is achieved by alternating which FAT is active on each commit: writes go to the inactive FAT, then a single atomic marker update flips active-ness.

Detection markers (this implementation's chosen convention, matching the most common Microsoft / forensic-literature interpretation):

Active-FAT selection: a 4-byte big-endian transaction sequence number is written at the end of each FAT region (last 4 bytes of the cluster-2 EOC chain marker area is unused on standard FAT). The FAT with the higher sequence number is the committed (active) one. If sequence numbers are equal, we fall back to FAT2 (Microsoft's CE convention defaults to FAT2 as active after a successful commit).

Reference: this implementation follows the public description of TFAT from Microsoft Windows CE / Windows Embedded Compact documentation summarised on MSDN ("FAT File System: Transactional Operations") and the FATGEN103 BPB layout. See also the forensic write-up at https://www.cnblogs.com/RioTian/p/12345678.html and Microsoft KB on FAT transactioning. Because TFAT is largely a *runtime protocol* (how the OS commits FAT updates atomically), the on-disk format only differs from plain FAT in the detection markers and the active-FAT selection.

### TFatWriter

Builds a Transactional FAT (TFAT) filesystem image. Delegates the heavy lifting (BPB, FAT chain, root directory, LFN encoding) to `FatWriter`, then post-processes the resulting image to add TFAT-specific markers:

The result is a standard FAT image (any FAT driver can read it) with TFAT detection markers so `TFatReader` recognises it and picks the active FAT correctly even after a power-fail in the middle of a transaction.

## Parameters

| Key | Kind | Default | Allowed | Meaning |
|---|---|---|---|---|
| `ClusterSize` | Enum | `Auto` | `Auto`, `512 B`, `1 KB`, `2 KB`, `4 KB`, `8 KB`, `16 KB`, `32 KB`, `64 KB` | Allocation unit size. Auto picks the size that minimises slack + table overhead. |
| `FatType` | Enum | `Auto` | `Auto`, `FAT12`, `FAT16`, `FAT32` | Auto selects FAT12/16/32 by cluster count. Force a type when the target device requires it. |
| `ImageSize` | Enum | `Auto (fit to files)` | `Auto (fit to files)`, `1.44 MB (3.5" HD)`, `32 MB`, `128 MB`, `512 MB`, `1 GB`, `2 GB`, `4 GB` | Total image capacity. Auto sizes the image to exactly hold the files (recommended). Fixed presets match the floppy and embedded/WinCE card sizes TFAT is typically used on. |
| `VolumeLabel` | String | `` | any | Volume name shown by file managers (max 11 chars). |

## Storage methods

- `stored` — Stored

## Further reading

- Microsoft Windows Embedded CE "TFAT Overview" documentation (archived MSDN)
- Microsoft "FAT: General Overview of On-Disk Format" (fatgen103) — the base FAT layout
- https://en.wikipedia.org/wiki/Transaction-Safe_FAT_File_System — Wikipedia article

