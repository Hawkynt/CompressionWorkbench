# Operation Coverage

Measures which of the user-facing maintenance operations each archive and
filesystem descriptor supports. An operation is "supported" when the descriptor
implements the corresponding capability interface (directly or via a base class).
The canonical definitions of these verbs live in
[`ARCHIVE-MODEL.md`](ARCHIVE-MODEL.md) → *The five maintenance verbs*; this file
is the coverage matrix.

| Operation       | Interface                                            | Meaning |
|-----------------|------------------------------------------------------|---------|
| Defragment      | `IArchiveDefragmentable` (+ `IFilesystemBlockMover`) | Re-order entries/extents so every file is contiguous; outer size preserved; contents byte-identical. |
| Shrink          | `IArchiveShrinkable`                                 | Keep the parameter set; minimise stored footprint (drop trailing free space / step to the smallest canonical size that still fits). |
| Optimize        | `ILayoutOptimizable`                                 | Find + apply the best layout parameters (cluster/block/inode size, geometry) via in-place patch or streaming rebuild; outer size preserved where possible. |
| Wipe            | `IWipeEmpty`                                         | Overwrite **only unused** space (free clusters, cluster-tip slack, deleted dir entries, padding, trailing junk); live data untouched; size preserved. |
| Purge           | `IArchiveModifiable.Remove`-all / empty `Create`     | Erase **all live** data, leaving a valid empty container. No dedicated interface yet (see ARCHIVE-MODEL → Naming note). |
| Metadata-reorder| `IFileInternalLayoutMap` / `IFileInternalChunkMover` | Move metadata chunks to a canonical/optimal position (e.g. streamable layout). File-internal containers. |

> **Naming:** earlier revisions of this file labelled `IWipeEmpty` as
> "Purge/Wipe"; under the canonical taxonomy that operation is **wipe** (it
> removes *dead* bytes). **Purge** is the distinct verb that removes *live* data.

All defrag / shrink / wipe operations preserve live file contents
byte-identical and keep the archive/image valid (the project's defrag
invariant: total logical content unchanged, files byte-identical, output still
round-trips and stays fsck-clean where a filesystem tool exists).

## Totals

| Operation        | Descriptors |
|------------------|-------------|
| Defragment       | 185 |
| Wipe             | 35  |
| Metadata-reorder | 9   |
| Shrink           | 7   |
| Optimize         | 2   |

(Descriptor-level counts. File-level `grep` counts read slightly higher because
some source files host a descriptor plus helper adapters.)

## Before / after this wave

This wave filled mainstream archive gaps. No operation was removed; live-content
invariants were verified by the per-operation tests under
`Compression.Tests/Operations/`.

| Operation | Before | After | Added |
|-----------|--------|-------|-------|
| Shrink    | 5      | 7     | **Zip**, **Tar** |
| Wipe      | 33     | 35    | **7z**, **Tar** |
| Defrag    | 185    | 185   | — |
| Optimize  | 2      | 2     | — |
| MetaReorder | 9    | 9     | — |

## Filesystem descriptors

| Format | Defrag | Shrink | Wipe | Optimize | MetaReorder |
|--------|:------:|:------:|:----:|:--------:|:-----------:|
| Adf | Y | Y | Y | N | N |
| AppleDos | Y | N | Y | N | N |
| Atari8 | Y | N | Y | N | N |
| Bbc | Y | N | Y | N | N |
| Btrfs | Y | N | Y | N | N |
| CpcDsk | Y | N | Y | N | N |
| CramFs | Y | N | Y | N | N |
| D64 | Y | Y | Y | N | N |
| D71 | Y | Y | Y | N | N |
| D81 | Y | Y | Y | N | N |
| DoubleSpace | Y | N | Y | N | N |
| DriveSpace3 | Y | N | Y | N | N |
| ExFat | Y | N | Y | N | N |
| Ext | Y | N | Y | Y | N |
| Ext1 | Y | N | Y | N | N |
| Fat | Y | Y | Y | N | N |
| Fatx | N | N | Y | Y | N |
| Hfs | Y | N | Y | N | N |
| HfsPlus | Y | N | Y | N | N |
| Iso | Y | N | Y | N | N |
| Jffs2 | Y | N | Y | N | N |
| Mfs | Y | N | Y | N | N |
| MinixFs | Y | N | Y | N | N |
| Msa | Y | N | Y | N | N |
| Ntfs | Y | N | Y | N | N |
| ProDos | Y | N | Y | N | N |
| RomFs | Y | N | Y | N | N |
| SquashFs | Y | N | Y | N | N |
| TrDos | Y | N | Y | N | N |
| Udf | Y | N | Y | N | N |
| Vdfs | Y | N | Y | N | N |
| Xfs | Y | N | Y | N | N |
| Adfs, AmigaPfs, ApplePascal, Coherent, Efs, Erofs, Gemdos, Gfs1, Hammer, Hammer2, Htfs, Human68k, Jfs, Jfs1, Lif, LittleFs, Nilfs1, Nilfs2, Ods1, OpenVms, Qnx4, Qnx6, Refs, Rt11, SysV, Ti99, Trsdos, Ubifs, Wafl, Xenix, Yaffs2, Zfs | N | N | Y | N | N |

Read-only / specialty filesystems that only expose Wipe are grouped on the last
row. Most read/write filesystems expose Defragment via the generic
`DefragRebuilder` (rebuild-via-WORM) path; only fixed-geometry disk-image
families (C64 D64/D71/D81, PC floppies via FAT, Amiga ADF) carry the multi-step
canonical-size Shrink.

## Archive / stream descriptors with at least one operation

| Format | Defrag | Shrink | Wipe | Optimize | MetaReorder |
|--------|:------:|:------:|:----:|:--------:|:-----------:|
| Zip | Y | **Y** | Y | N | N |
| SevenZip (7z) | Y | N | **Y** | N | N |
| Tar | Y | **Y** | **Y** | N | N |
| Mp3 | Y | N | N | N | Y |
| Mp4 | N | N | N | N | Y |
| Avi | N | N | N | N | Y |
| Matroska | N | N | N | N | Y |
| Ogg | N | N | N | N | Y |
| Wav | Y | N | N | N | Y |
| Png / Tiff / Jpeg (Crush adapters) | N | N | N | N | Y |

Bold cells were added in this wave. ~175 further archive descriptors implement
Defragment only (via `DefragRebuilder`); they are counted in the totals but not
listed individually here.

## N/A notes

- **Shrink on fixed-geometry images** (most read-only filesystems, optical
  images): no smaller canonical size exists, so Shrink is intentionally absent.
- **Metadata-reorder on archives with a single central directory** (Zip, 7z):
  Defragment already lands the central directory in its canonical contiguous
  trailing position, so a separate reorder pass would be a no-op.
- **Cluster-tip wiping on solid/streamed containers** (7z, Tar): there is no
  per-file cluster slack — the wiper zeros inter-block gaps and trailing junk
  only.
