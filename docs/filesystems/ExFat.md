# exFAT (`ExFat`)

exFAT filesystem image

> Generated from the implementation. Edit the doc comments on the descriptor,
> reader or writer rather than this file; a test regenerates it and fails on drift.

## At a glance

| | |
|---|---|
| Category | Archive |
| Family | Archive |
| Default extension | `.img` |
| Recognised extensions | `.img`, `.exfat` |

## Detection

| Bytes | At offset | Confidence |
|---|---|---|
| `45 58 46 41 54 20 20 20` | 3 | 0.90 |

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

By moving what is out of place, through `ExFatBlockMover`.
A run is copied and whatever records its position is rewritten, so the cost is
the bytes that actually move rather than the whole volume.

| Property | Value | Meaning |
|---|---|---|
| Repoints runs independently | no | whether a file in several pieces can be moved one piece at a time |
| Relinks a whole allocation | yes | whether a scattered file's chain can be restated in one call |
| Holds runs outside the volume | yes | whether a full volume can be rearranged by lifting a run into memory |

## How a volume is laid out

### ExFatFormatDescriptor

References:

### ExFatReader

Reads exFAT filesystem images. Parses VBR, FAT, and directory entry sets (File 0x85 + Stream Extension 0xC0 + File Name 0xC1). Supports subdirectories.

### ExFatWriter

Builds exFAT filesystem images that Windows 10+ actually mounts.

Default layout: 8 MB image, 512 B/sector, 8 sectors/cluster (4 KB clusters). VBR at sector 0, backup VBR at sector 12, FAT at sector 24, cluster heap thereafter; cluster 2 = root, cluster 3 = allocation bitmap, cluster 4 = up-case table.

Key real-world fixes over the original implementation: Set-checksum on each File directory entry set (required — Windows silently ignores files whose set-checksum is wrong), up-case table checksum, timestamps on create/modify/access, volume serial number, filesystem revision (1.0), stream-extension GeneralSecondaryFlags advertising FAT-chain allocation. These are the fields fsck/chkdsk and diskutil/fsck_exfat audit before declaring the volume clean.

### ExFatExtentMap

Walks an exFAT image and yields its actual on-disk byte layout — the reserved boot region (VBR + backup VBR + OEM parameters), the FAT, every cluster-chain run per file, and the free-cluster set. Honours the FAT-chain bypass bit (NoFatChain) for contiguous extent shortcuts.

Streaming: reads only the VBR + dir clusters from disk. FAT navigation flows through a `SectorCache` so a 50 TB exFAT image with a 50 GB FAT keeps memory bounded to ~256 MB.

## Parameters

| Key | Kind | Default | Allowed | Meaning |
|---|---|---|---|---|
| `ClusterSize` | Enum | `Auto` | `Auto`, `4 KB`, `8 KB`, `16 KB`, `32 KB`, `64 KB`, `128 KB` | Allocation unit size. Auto picks the size that minimises slack + FAT overhead for the files being stored. Larger clusters reduce FAT overhead but waste more space per file. |
| `ImageSize` | Enum | `Auto (fit to files)` | `Auto (fit to files)`, `32 MB`, `128 MB`, `256 MB`, `512 MB`, `1 GB`, `2 GB`, `4 GB`, `16 GB`, `32 GB`, `128 GB` | Total image capacity. Auto sizes the image to exactly hold the files (recommended). |
| `VolumeLabel` | String | `` | any | Volume name (max 15 chars, Unicode). |

## Storage methods

- `stored` — Stored

## Further reading

- https://learn.microsoft.com/en-us/windows/win32/fileio/exfat-specification — Microsoft's official exFAT file system specification
- https://github.com/torvalds/linux/tree/master/fs/exfat — mainline kernel implementation
- https://en.wikipedia.org/wiki/ExFAT — Wikipedia overview

