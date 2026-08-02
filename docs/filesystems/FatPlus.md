# FAT+ Filesystem Image (large-file extension) (`FatPlus`)

FAT32/FAT16 image with the FAT+ 256 GiB-file extension (FATPLUS.TXT draft rev 2/3).

> Generated from the implementation. Edit the doc comments on the descriptor,
> reader or writer rather than this file; a test regenerates it and fails on drift.

## At a glance

| | |
|---|---|
| Category | Archive |
| Family | Archive |
| Default extension | `.img` |
| Recognised extensions | none |

## Detection

| Bytes | At offset | Confidence |
|---|---|---|
| `46 41 54 2B 20 20 20 20` | 3 | 0.95 |

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

### FatPlusFormatDescriptor

FAT+ (also called FAT32+ / FAT16+) format descriptor. FAT+ is an open extension to standard FAT that lifts the per-file 4 GiB size cap to 256 GiB by repurposing previously-reserved bytes in the 32-byte directory entry to hold the upper bits of file size. References:

Specification source. FAT+ draft revision 2/3 (FATPLUS.TXT, 2007) by Udo Kuhnt, Luchezar Georgiev and Jeremy Davis, historically hosted at fdos.org/kernel/fatplus.txt. Cited from the Wikipedia "File Allocation Table" and "Large-file support" articles.

Detection. A FAT+ volume is identified by an OEM-name signature in the BPB: the 8 ASCII bytes at offset 3 of the boot sector read "FAT+ " (4 chars + 4 spaces). This descriptor uses that as a magic signature with high confidence — the standard FAT descriptor has no magic and falls back to extension matching, so this descriptor is always tried first.

Implemented operations. List, extract, create, add, remove, and defragment. Creation produces a FAT32 image with the FAT+ OEM signature and per-file 38-bit size encoding (low 32 bits at DIR_FileSize, high 6 bits in the low 6 bits of DIR_NTRes; top 2 bits of NTRes remain clear to preserve the Windows NT case-flag convention). Add/Remove operate genuinely in place via `FatPlusInPlaceAdder` (Add allocates free clusters, links the chain, inserts the dirent and patches the FAT+ extended-size bits; Remove frees the chain + wipes the dirent), with a verified `FatPlusWriter` rebuild as the structural-edge-case fallback. Defragment goes through the standard `DefragRebuilder` rebuild path.

### FatPlusReader

Read-only reader for FAT+ filesystem images. FAT+ is a backward-compatible extension to FAT32 (and FAT16) that lifts the 4 GiB per-file size limit by repurposing previously-reserved bytes in the 32-byte directory entry to hold the upper bits of an extended file-size value.

Specification source. The FAT+ draft specification (FATPLUS.TXT, revisions 2 and 3, 2007) was authored by Udo Kuhnt, Luchezar Georgiev and Jeremy Davis, and is historically hosted under fdos.org. It is referenced from the Wikipedia "File Allocation Table" / "Large-file support" articles. The draft documents a file-size extension that pushes the cap to 256 GiB - 1 byte (2^38 - 1) on otherwise spec-compliant FAT32 (and FAT16) volumes.

Volume identification. A FAT+ volume is marked by an OEM-name signature in the BPB: bytes 3..10 (the 8-byte BS_OEMName field) read "FAT+ " (4 ASCII chars + 4 spaces). Standard FAT32 readers ignore the OEM string, so non-aware readers still see the underlying FAT32 layout and can list files whose sizes fit in 32 bits — they only mis-read files > 4 GiB (the size field appears truncated and the cluster chain looks over-long).

Directory-entry layout. The standard 32-byte FAT directory entry is unchanged in placement; only previously-reserved bytes are used for the extended size field. This implementation follows the most widely documented FAT+ rev 2/3 variant: The resulting 38-bit size field caps file size at 2^38 − 1 = 256 GiB − 1 byte, matching the documented FAT+ limit.

Compatibility caveats.

### FatPlusWriter

Builds FAT+ filesystem images. FAT+ is a backward-compatible extension to standard FAT32 (and FAT16) that lifts the 4 GiB per-file size cap to 256 GiB by repurposing the low 6 bits of the otherwise-reserved `DIR_NTRes` byte (offset 12) of the 32-byte directory entry as the high 6 bits of the file size — together with the standard 32-bit `DIR_FileSize` at offset 28 this forms a 38-bit size field. The OEM-name string in the BPB is set to `"FAT+ "` (offset 3..10) so FAT+-aware readers see the extension.

Implementation strategy. This writer wraps `FatWriter` to produce the underlying FAT32 image, then patches:

Extended sizes for tests. The optional extendedSize parameter on `AddFile` allows storing a file whose declared size exceeds the actual data bytes — the cluster chain only carries data.Length bytes but the directory entry reports the larger extended size. This is the pragma testers use to exercise the 38-bit encoding without writing actual >4 GiB payloads; a FAT+-aware reader will stop at end-of-chain rather than invent missing bytes.

## Parameters

| Key | Kind | Default | Allowed | Meaning |
|---|---|---|---|---|
| `ClusterSize` | Enum | `Auto` | `Auto`, `512 B`, `1 KB`, `2 KB`, `4 KB`, `8 KB`, `16 KB`, `32 KB`, `64 KB` | Allocation unit size. Auto picks the size that minimises slack + FAT overhead. |
| `ImageSize` | Enum | `Auto (fit to files)` | `Auto (fit to files)`, `512 MB`, `1 GB`, `2 GB`, `4 GB`, `16 GB`, `64 GB` | Total image capacity. Auto fits the files (minimum 100 MB to stay in FAT32). FAT+ targets large volumes, so the fixed presets start at 512 MB. |
| `VolumeLabel` | String | `` | any | Volume name shown by file managers (max 11 chars). |

## Storage methods

- `stored` — Stored

## Further reading

- FAT+ draft revision 2 (FATPLUS.TXT, Udo Kuhnt / Luchezar Georgiev / Jeremy Davis, 2007) — the defining spec, historically hosted at fdos.org/kernel/fatplus.txt
- https://en.wikipedia.org/wiki/Design_of_the_FAT_file_system — Wikipedia's FAT reference, which documents the FAT+ extension

