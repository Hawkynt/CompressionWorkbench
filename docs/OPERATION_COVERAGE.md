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

> **Composite verb:** **compact** is not in this matrix because it has no
> interface of its own — it chains *defrag → optimize → shrink* (or, with
> `--minimal`, a single minimal-geometry rebuild). A format gets a compact action
> when it implements at least one of those three. See
> [`ARCHIVE-MODEL.md`](ARCHIVE-MODEL.md) → *compact — the one-click composite*.

> **Naming:** earlier revisions of this file labelled `IWipeEmpty` as
> "Purge/Wipe"; under the canonical taxonomy that operation is **wipe** (it
> removes *dead* bytes). **Purge** is the distinct verb that removes *live* data.

All defrag / shrink / wipe operations preserve live file contents
byte-identical and keep the archive/image valid (the project's defrag
invariant: total logical content unchanged, files byte-identical, output still
round-trips and stays fsck-clean where a filesystem tool exists).

## Totals (live registry, all categories)

| Operation        | Descriptors |
|------------------|-------------|
| Defragment       | 229 |
| Wipe             | 181 |
| Purge            | 154 |
| Shrink           | 91  |
| Optimize (layout)| 43  |
| Metadata-reorder | 6   |

(Counts are `GetArchiveOps(id) is IXxx` over the registered descriptors — i.e.
what the UI/CLI actually gate on. "Wipe" counts the direct `IWipeEmpty`
implementers plus the `IFilesystemExtentMap` / `IArchiveLayoutMap` fallback the
wiper accepts. "Purge" counts `IArchiveModifiable` (Remove-all).)

## Default-mechanism rollout

Most maintenance verbs no longer require bespoke per-format code. The capability
interfaces carry **default implementations** backed by a verified, round-trip-checked
extract → re-create engine (`Compression.Registry.RebuildVerb`):

- **`IArchiveShrinkable.Shrink`** — default rebuild (auto-fit / tight-pack); never
  grows, never throws, never corrupts (emits the original unchanged if the rebuild
  isn't smaller or fails).
- **`IArchiveDefragmentable.Defragment`** — default verified in-place rebuild.
- **`IArchiveModifiable.Add` / `Remove`** — default verified extract→edit→re-create;
  `Remove(all)` is the **purge** verb.

A filesystem descriptor therefore gains shrink / defrag / purge by simply declaring
the interface (it already implements `IArchiveFormatOperations` + `IArchiveCreatable`).
Bespoke in-place implementations still override the default for efficiency. Coverage
is guarded by the registry-parametrised `Generic{Shrink,Defrag,Purge}RoundTripTests`
under `Compression.Tests/Operations/`, which fail loudly on any lossy rebuild.

- **`ILayoutOptimizable`** now carries the same kind of default (verified rebuild
  honouring `LayoutRebuildOptions` geometry), rolled out to **43** filesystems
  (was 3), guarded by `GenericLayoutOptimizableTests`.
- **`reconfigure`** (`Compression.Lib.ReconfigureOperation`, `cwb reconfigure --set
  Key=Value`, and the UI *Maintenance → Reconfigure* entry) re-applies geometry/options
  to an *existing* image (e.g. NTFS MFT-record size, cluster size, FAT root entries)
  via the verified rebuild — contents preserved, only geometry changes.
- **NTFS per-file compression**: the `Compression` create option (`Off`/`LZNT1`)
  stores files in a compressed `$DATA` attribute; small files stay resident in the MFT.
- **Creation-option schemas** (`IFormatOptionsSchema`) now cover **75 of 89** creatable
  filesystems (was 43; Ufs gained a `VolumeLabel` → `fs_volname` knob, and PS1 memory
  cards expose their bank count). The remaining 14 —
  Bfs, Coherent, CramFs, DragonFs, G64, Hpfs, MinixFs, Msa, Qnx4, Qnx6, Vdfs, Xenix,
  Yaffs2, ZxScl — are intentionally schema-less for concrete reasons, not laziness:
  - **Coherent** — `s_fname`/`s_fpack` are the format's *detection signature*
    (`"noname"/"nopack"`); a custom value would break recognition.
  - **MinixFs** — no volume label; block size is standard-fixed at 1024 (mainstream
    `mkfs.minix`), and non-1024 minix v3 is frequently unmountable.
  - **Hpfs** — HPFS stores no volume label as a simple field; it'd be a structural feature.
  - **CramFs** — non-standard superblock root-inode offset; adding the spec `name[16]`
    is a layout correction (regression risk), not a knob.
  - The rest (Bfs, DragonFs, G64, Msa, Qnx4/6, Vdfs, Xenix, Yaffs2, ZxScl) are
    fixed-geometry / detection-constrained writers with no user-tunable on-disk field.
  No fake/no-op knobs are exposed; every published option is verified by a per-format
  test that the knob takes effect on disk.

## Write capability — WORM vs R/W (an honesty rule)

Write capability is a four-level scale (`Compression.Registry.FormatCapabilities`):

| Level | Flags | Meaning |
|-------|-------|---------|
| Unsupported | — | no descriptor. |
| Read-Only | `CanList` / `CanExtract` | inspect + extract only. |
| **WORM** (Write-Once-Read-Many) | `+ CanCreate` | a fresh image is produced from inputs; an existing instance is not offered for modification. |
| **R/W** | `+ CanModify` | an existing container can be modified (add / replace / remove), producing a valid result. |

**R/W means a working modify on an existing container — the edit may be byte-preserving
in place OR may relayout / re-pack the container (moving existing data).** Both are honest
R/W for a *conceptually read-write* format; an edit that has to move data is still R/W, not a
"fake". The maintenance verbs (add / remove / purge / defragment / shrink) are realised either
by a format-specific modifier or by the verified extract → re-create rebuild (`RebuildVerb` /
`ModifyRebuilder`, or the default `IArchiveModifiable` members).

> **`CanModify` is advertised when the workbench has a proven existing-instance edit path.**
> An operating system mounting a filesystem read-only does not make an offline image editor
> WORM: CramFS, SquashFS and EROFS remain read-only when mounted by Linux while their supported
> workbench profiles are R/W by verified rebuild. `CanModify` is withheld when no such edit path
> exists, not merely because the native mount policy is immutable.

`Compression.Tests.Operations.WriteCapabilityHonestyTests` enforces the deterministic half:
every `CanModify` claimant's ops must implement `IArchiveModifiable` (a real modify path —
no unbacked flag). That the modify *works* (round-trips) is covered by the registry-driven
`Generic{Purge,Defrag,Shrink}RoundTripTests`.

### R/W realisation per format

- **Byte-preserving in place** (existing data stays put): FAT12/16/32 (`FatModifier`), GEMDOS,
  GS/OS, exFAT, ext, HFS/HFS+, APFS, F2FS, JFS, UFS, UDF, the log-structured
  JFFS2/YAFFS2/UBIFS/NILFS2, the CVF family, the retro disk formats; PS1 memory-card
  deletion (which marks directory records deleted while retaining recoverable save blocks);
  the in-place archive editors (ZIP family, TAR, AR, CPIO, XAR, LZH/LHA, ARJ, ZOO, PDF);
  byte-identity append (Ghost); the sector-image editors (BIN/CUE, CDI, MDF, NRG, CSO); and
  the disk-image containers that delegate to a R/W inner filesystem (QCOW2/VHD/VHDX/VMDK/VDI).
- **Relayout / re-pack** (valid result, existing data may move): **NTFS, XFS, Btrfs, ReiserFS,
  CramFS, SquashFS, EROFS** and PS1 memory-card add/replace/defrag (the supported image is
  rebuilt or re-packed and verified), plus **7-Zip, CAB, RAR** (the solid streams are rewritten
  via the extract → re-create rebuild; RAR re-emits a valid RAR5 via `RarWriter` and recomputes
  every CRC — so the cross-referencing-checksum concern of an append-style edit does not apply).

### Stays WORM (create-only)

- **Wim**, **Swm** — checksum-record archives kept create-only: there is no in-place
  editor, and an append-style edit would corrupt the cross-referencing checksum chain
  (see `ChecksumRecordArchiveReadOnlyContractTests`). Sqx and Ace belong to the same
  family but do carry an existing-instance editor — the verified extract → edit →
  re-create rebuild — and so advertise R/W; the checksum chain is re-derived rather
  than appended to.
- **Wrapster**, **Ova**, **Mfs1**, **Stacker** keep the rebuild-backed verb without
  advertising R/W, because each rejects an arbitrary edited member set: Wrapster
  carries one MP3, OVA mandates a manifest over all members, MFS-1 and Stacker write
  a bespoke catalog their own reader must still accept.

## Filesystem descriptors

Generated from the live registry (113 filesystem descriptors). **Compact** is the
composite verb (defrag + optimize + shrink, or a `--minimal` geometry rebuild) and
is available whenever any of defrag/shrink/create is — see [`ARCHIVE-MODEL.md`](ARCHIVE-MODEL.md).
Wipe counts the `IWipeEmpty` implementers plus the extent/layout-map fallback;
filesystems without an extent map cannot expose a true in-place forensic wipe.

| Format | Compact | Defrag | Shrink | Purge | Wipe | Optimize |
|--------|:------:|:------:|:------:|:-----:|:----:|:--------:|
| Adf | Y | Y | Y | Y | Y | Y |
| Adfs | Y | Y | Y | Y | Y | Y |
| AdvFs | Y | Y | Y | Y | Y | Y |
| AmigaPfs | Y | Y | Y | Y | Y | Y |
| Apfs | Y | Y | Y | Y | Y | Y |
| AppleDos | Y | Y | Y | Y | Y | Y |
| ApplePascal | Y | Y | Y | Y | Y | Y |
| Atari8 | Y | Y | Y | Y | Y | Y |
| Bbc | Y | Y | Y | Y | Y | Y |
| BcacheFs | Y | Y | Y | Y | Y | Y |
| BeeGfs | · | · | · | · | · | · |
| Bfs | Y | Y | Y | Y | Y | Y |
| Btrfs | Y | Y | Y | Y | Y | Y |
| CephFs | · | · | · | · | · | · |
| Coherent | Y | Y | Y | Y | Y | · |
| CpcDsk | Y | Y | Y | Y | Y | Y |
| Cpm | Y | Y | Y | Y | Y | Y |
| CramFs | Y | Y | Y | Y | Y | Y |
| Cromemco | Y | Y | Y | Y | Y | Y |
| Cxfs | · | · | · | · | · | · |
| D64 | Y | Y | Y | Y | Y | Y |
| D71 | Y | Y | Y | Y | Y | Y |
| D81 | Y | Y | Y | Y | Y | Y |
| DoubleSpace | Y | Y | Y | Y | Y | Y |
| DragonFs | Y | Y | Y | Y | Y | · |
| DriveSpace | Y | Y | Y | Y | Y | Y |
| DriveSpace3 | Y | Y | Y | Y | Y | Y |
| Ecryptfs | Y | Y | · | · | · | · |
| Efs | Y | Y | Y | Y | Y | Y |
| Erofs | Y | Y | Y | Y | Y | Y |
| ExFat | Y | Y | Y | Y | Y | Y |
| Ext | Y | Y | Y | Y | Y | Y |
| Ext1 | Y | Y | Y | Y | Y | Y |
| F2fs | Y | Y | Y | Y | Y | Y |
| Fat | Y | Y | Y | Y | Y | Y |
| FatPlus | Y | Y | Y | Y | Y | Y |
| Fatx | Y | Y | Y | Y | Y | Y |
| G64 | Y | Y | Y | Y | Y | · |
| Gemdos | Y | Y | Y | Y | Y | Y |
| Gfs1 | Y | Y | Y | Y | Y | Y |
| Gfs2 | Y | Y | Y | Y | Y | Y |
| GlusterFs | · | · | · | · | · | · |
| Gpfs | · | · | · | · | · | · |
| GsOs | Y | Y | Y | Y | · | · |
| Hammer | Y | Y | Y | Y | Y | Y |
| Hammer2 | Y | Y | Y | Y | Y | Y |
| Hfs | Y | Y | Y | Y | Y | Y |
| HfsPlus | Y | Y | Y | Y | Y | Y |
| Hpfs | Y | Y | Y | Y | Y | Y |
| Htfs | Y | Y | Y | Y | Y | Y |
| Human68k | Y | Y | Y | Y | Y | Y |
| Iso | Y | Y | Y | Y | Y | Y |
| Jffs2 | Y | Y | Y | Y | Y | Y |
| Jfs | Y | Y | Y | Y | Y | Y |
| Jfs1 | Y | Y | Y | Y | Y | Y |
| JuiceFs | · | · | · | · | · | · |
| Lif | Y | Y | Y | Y | Y | Y |
| LittleFs | Y | Y | Y | Y | Y | Y |
| Lustre | · | · | · | · | · | · |
| Mfs | Y | Y | Y | Y | Y | Y |
| Mfs1 | Y | Y | Y | Y | Y | Y |
| MinixFs | Y | Y | Y | Y | Y | Y |
| MinixV1 | Y | Y | Y | Y | Y | Y |
| MinixV2 | Y | Y | Y | Y | Y | Y |
| MooseFs | · | · | · | · | · | · |
| Msa | Y | Y | Y | Y | Y | · |
| Nib | Y | Y | · | Y | Y | · |
| Nilfs1 | Y | Y | Y | Y | Y | Y |
| Nilfs2 | Y | Y | Y | Y | Y | Y |
| Nss | Y | Y | · | · | Y | · |
| Ntfs | Y | Y | Y | Y | Y | Y |
| Nwfs | · | · | · | · | · | · |
| Nwfs386 | · | · | · | · | · | · |
| Ocfs2 | Y | Y | Y | Y | Y | Y |
| Ods1 | Y | Y | Y | Y | Y | Y |
| OneFs | · | · | · | · | · | · |
| OpenVms | Y | Y | Y | Y | Y | Y |
| OrangeFs | Y | Y | · | Y | · | · |
| Os9Rbf | Y | Y | Y | Y | Y | Y |
| Pc98 | Y | Y | Y | Y | Y | Y |
| ProDos | Y | Y | Y | Y | Y | Y |
| Ps1MemoryCard | Y | Y | Y | Y | Y | · |
| Qnx4 | Y | Y | Y | Y | Y | Y |
| Qnx6 | Y | Y | Y | Y | Y | Y |
| Refs | Y | Y | · | Y | Y | Y |
| Reiser4 | Y | Y | Y | Y | Y | Y |
| ReiserFs | Y | Y | Y | Y | Y | Y |
| RomFs | Y | Y | Y | Y | Y | Y |
| Rt11 | Y | Y | Y | Y | Y | Y |
| Sfs | Y | Y | · | Y | Y | · |
| SmartFs | Y | Y | · | Y | Y | · |
| SquashFs | Y | Y | Y | Y | Y | Y |
| Stacker | Y | Y | Y | Y | Y | Y |
| SysV | Y | Y | Y | Y | Y | Y |
| TahoeLafs | Y | Y | · | · | · | · |
| TFat | Y | Y | Y | Y | Y | Y |
| Tfs | · | · | · | · | · | · |
| Ti99 | Y | Y | Y | Y | Y | Y |
| TrDos | Y | Y | Y | Y | Y | Y |
| Trsdos | Y | Y | Y | Y | Y | Y |
| Tux2 | Y | Y | Y | Y | Y | Y |
| Tux3 | Y | Y | Y | Y | Y | Y |
| Ubifs | Y | Y | Y | Y | Y | Y |
| Udf | Y | Y | Y | Y | Y | Y |
| Ufs | Y | Y | Y | Y | Y | Y |
| Vdfs | Y | Y | Y | Y | Y | Y |
| VxFs | Y | Y | · | Y | Y | · |
| Wafl | · | · | · | · | · | · |
| Xenix | Y | Y | Y | Y | Y | Y |
| Xfs | Y | Y | Y | Y | Y | Y |
| Yaffs2 | Y | Y | Y | Y | Y | Y |
| Zfs | Y | Y | Y | Y | · | Y |
| ZxScl | Y | Y | Y | Y | Y | · |

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
