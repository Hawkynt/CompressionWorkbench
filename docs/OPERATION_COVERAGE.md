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
| Optimize        | `ILayoutOptimizable` / tunable `IFormatOptionsSchema` | Search executable layout/compression parameters and keep the smallest/best verified result. |
| Wipe            | `IWipeEmpty`                                         | Overwrite **only unused** space (free clusters, cluster-tip slack, deleted dir entries, padding, trailing junk); live data untouched; size preserved. |
| Purge           | `IArchivePurgeable`                                  | Erase all live user data, leaving a valid empty container/image; system metadata may be recreated as required by the format. |
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
| Defragment       | 243 |
| Wipe             | 225 |
| Purge            | 223 |
| Shrink           | 96 |
| Optimize (layout) | 101 |
| Metadata-reorder | 9 |

(Counts are regenerated from explicit descriptor capability interfaces in this tree. Runtime marker/flag consistency is enforced by CI; the UI/CLI gate on the same capability contracts. "Wipe" counts the direct `IWipeEmpty`
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
  `IArchivePurgeable.Purge` is the **purge** verb; `IArchiveModifiable` inherits it because full modification includes removing all live user files.

A filesystem descriptor therefore gains shrink / defrag / purge by declaring
the corresponding interface (it already implements `IArchiveFormatOperations` + `IArchiveCreatable`).
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

Generated from the descriptor capability interfaces in this tree. `☑` means the operation is exposed and backed by an implementation; `☐` means it is not exposed. **Compact** is the composite defrag → optimize → shrink action (or a create-backed minimal-geometry rebuild). A checked operation may be native/in-place or a verified offline rebuild; mounted-driver R/W readiness is tracked separately.

| Format | Compact | Defrag | Shrink | Purge | Wipe | Optimize |
| --- | :---: | :---: | :---: | :---: | :---: | :---: |
| Adf | ☑ | ☑ | ☑ | ☑ | ☑ | ☑ |
| Adfs | ☑ | ☑ | ☑ | ☑ | ☑ | ☑ |
| AdvFs | ☑ | ☑ | ☑ | ☑ | ☑ | ☑ |
| AmigaPfs | ☑ | ☑ | ☑ | ☑ | ☑ | ☑ |
| Apfs | ☑ | ☑ | ☑ | ☑ | ☑ | ☑ |
| AppleDos | ☑ | ☑ | ☑ | ☑ | ☑ | ☑ |
| ApplePascal | ☑ | ☑ | ☑ | ☑ | ☑ | ☑ |
| Atari8 | ☑ | ☑ | ☑ | ☑ | ☑ | ☑ |
| Bbc | ☑ | ☑ | ☑ | ☑ | ☑ | ☑ |
| BcacheFs | ☑ | ☑ | ☑ | ☑ | ☑ | ☑ |
| BeeGfs | ☐ | ☐ | ☐ | ☐ | ☐ | ☐ |
| Bfs | ☑ | ☑ | ☑ | ☑ | ☑ | ☑ |
| Btrfs | ☑ | ☑ | ☑ | ☑ | ☑ | ☑ |
| CephFs | ☐ | ☐ | ☐ | ☐ | ☐ | ☐ |
| Coherent | ☑ | ☑ | ☑ | ☑ | ☑ | ☐ |
| CpcDsk | ☑ | ☑ | ☑ | ☑ | ☑ | ☑ |
| Cpm | ☑ | ☑ | ☑ | ☑ | ☑ | ☑ |
| CramFs | ☑ | ☑ | ☑ | ☑ | ☑ | ☑ |
| Cromemco | ☑ | ☑ | ☑ | ☑ | ☑ | ☑ |
| Cxfs | ☐ | ☐ | ☐ | ☐ | ☐ | ☐ |
| D64 | ☑ | ☑ | ☑ | ☑ | ☑ | ☑ |
| D71 | ☑ | ☑ | ☑ | ☑ | ☑ | ☑ |
| D81 | ☑ | ☑ | ☑ | ☑ | ☑ | ☑ |
| DoubleSpace | ☑ | ☑ | ☑ | ☑ | ☑ | ☑ |
| DragonFs | ☑ | ☑ | ☑ | ☑ | ☑ | ☐ |
| DriveSpace | ☑ | ☑ | ☑ | ☑ | ☑ | ☑ |
| DriveSpace3 | ☑ | ☑ | ☑ | ☑ | ☑ | ☑ |
| Ecryptfs | ☐ | ☐ | ☐ | ☐ | ☐ | ☐ |
| Efs | ☑ | ☑ | ☑ | ☑ | ☑ | ☑ |
| Erofs | ☑ | ☑ | ☑ | ☑ | ☑ | ☑ |
| ExFat | ☑ | ☑ | ☑ | ☑ | ☑ | ☑ |
| Ext | ☑ | ☑ | ☑ | ☑ | ☑ | ☑ |
| Ext1 | ☑ | ☑ | ☑ | ☑ | ☑ | ☑ |
| F2fs | ☑ | ☑ | ☑ | ☑ | ☑ | ☑ |
| Fat | ☑ | ☑ | ☑ | ☑ | ☑ | ☑ |
| FatPlus | ☑ | ☑ | ☑ | ☑ | ☑ | ☑ |
| Fatx | ☑ | ☑ | ☑ | ☑ | ☑ | ☑ |
| G64 | ☑ | ☑ | ☑ | ☑ | ☑ | ☐ |
| Gemdos | ☑ | ☑ | ☑ | ☑ | ☑ | ☑ |
| Gfs1 | ☑ | ☑ | ☑ | ☑ | ☑ | ☑ |
| Gfs2 | ☑ | ☑ | ☑ | ☑ | ☑ | ☑ |
| GlusterFs | ☐ | ☐ | ☐ | ☐ | ☐ | ☐ |
| Gpfs | ☐ | ☐ | ☐ | ☐ | ☐ | ☐ |
| GsOs | ☑ | ☑ | ☑ | ☑ | ☐ | ☐ |
| Hammer | ☑ | ☑ | ☑ | ☑ | ☑ | ☑ |
| Hammer2 | ☑ | ☑ | ☑ | ☑ | ☑ | ☑ |
| Hfs | ☑ | ☑ | ☑ | ☑ | ☑ | ☑ |
| HfsPlus | ☑ | ☑ | ☑ | ☑ | ☑ | ☑ |
| Hpfs | ☑ | ☑ | ☑ | ☑ | ☑ | ☑ |
| Htfs | ☑ | ☑ | ☑ | ☑ | ☑ | ☑ |
| Human68k | ☑ | ☑ | ☑ | ☑ | ☑ | ☑ |
| Iso | ☑ | ☑ | ☑ | ☑ | ☑ | ☑ |
| Jffs2 | ☑ | ☑ | ☑ | ☑ | ☑ | ☑ |
| Jfs | ☑ | ☑ | ☑ | ☑ | ☑ | ☑ |
| Jfs1 | ☑ | ☑ | ☑ | ☑ | ☑ | ☑ |
| JuiceFs | ☐ | ☐ | ☐ | ☐ | ☐ | ☐ |
| Lif | ☑ | ☑ | ☑ | ☑ | ☑ | ☑ |
| LittleFs | ☑ | ☑ | ☑ | ☑ | ☑ | ☑ |
| Lustre | ☐ | ☐ | ☐ | ☐ | ☐ | ☐ |
| Mfs | ☑ | ☑ | ☑ | ☑ | ☑ | ☑ |
| Mfs1 | ☑ | ☑ | ☑ | ☑ | ☑ | ☑ |
| MinixFs | ☑ | ☑ | ☑ | ☑ | ☑ | ☑ |
| MinixV1 | ☑ | ☑ | ☑ | ☑ | ☑ | ☑ |
| MinixV2 | ☑ | ☑ | ☑ | ☑ | ☑ | ☑ |
| MooseFs | ☐ | ☐ | ☐ | ☐ | ☐ | ☐ |
| Msa | ☑ | ☑ | ☑ | ☑ | ☑ | ☐ |
| Nilfs1 | ☑ | ☑ | ☑ | ☑ | ☑ | ☑ |
| Nilfs2 | ☑ | ☑ | ☑ | ☑ | ☑ | ☑ |
| Nss | ☑ | ☑ | ☐ | ☐ | ☑ | ☐ |
| Ntfs | ☑ | ☑ | ☑ | ☑ | ☑ | ☑ |
| Nwfs | ☐ | ☐ | ☐ | ☐ | ☐ | ☐ |
| Nwfs386 | ☐ | ☐ | ☐ | ☐ | ☐ | ☐ |
| Ocfs2 | ☑ | ☑ | ☑ | ☑ | ☑ | ☑ |
| Ods1 | ☑ | ☑ | ☑ | ☑ | ☑ | ☑ |
| OneFs | ☐ | ☐ | ☐ | ☐ | ☐ | ☐ |
| OpenVms | ☑ | ☑ | ☑ | ☑ | ☑ | ☑ |
| OrangeFs | ☑ | ☑ | ☐ | ☑ | ☐ | ☐ |
| Os9Rbf | ☑ | ☑ | ☑ | ☑ | ☑ | ☑ |
| Pc98 | ☑ | ☑ | ☑ | ☑ | ☑ | ☑ |
| ProDos | ☑ | ☑ | ☑ | ☑ | ☑ | ☑ |
| Ps1MemoryCard | ☑ | ☑ | ☑ | ☑ | ☑ | ☐ |
| Qnx4 | ☑ | ☑ | ☑ | ☑ | ☑ | ☑ |
| Qnx6 | ☑ | ☑ | ☑ | ☑ | ☑ | ☑ |
| Refs | ☑ | ☑ | ☐ | ☑ | ☑ | ☑ |
| Reiser4 | ☑ | ☑ | ☑ | ☑ | ☑ | ☑ |
| ReiserFs | ☑ | ☑ | ☑ | ☑ | ☑ | ☑ |
| RomFs | ☑ | ☑ | ☑ | ☑ | ☑ | ☑ |
| Rt11 | ☑ | ☑ | ☑ | ☑ | ☑ | ☑ |
| Sfs | ☑ | ☑ | ☐ | ☐ | ☑ | ☐ |
| SmartFs | ☑ | ☑ | ☑ | ☑ | ☑ | ☑ |
| SquashFs | ☑ | ☑ | ☑ | ☑ | ☑ | ☑ |
| Stacker | ☑ | ☑ | ☑ | ☑ | ☑ | ☑ |
| SysV | ☑ | ☑ | ☑ | ☑ | ☑ | ☑ |
| TahoeLafs | ☐ | ☐ | ☐ | ☐ | ☐ | ☐ |
| TFat | ☑ | ☑ | ☑ | ☑ | ☑ | ☑ |
| Tfs | ☐ | ☐ | ☐ | ☐ | ☐ | ☐ |
| Ti99 | ☑ | ☑ | ☑ | ☑ | ☑ | ☑ |
| TrDos | ☑ | ☑ | ☑ | ☑ | ☑ | ☑ |
| Trsdos | ☑ | ☑ | ☑ | ☑ | ☑ | ☑ |
| Tux2 | ☑ | ☑ | ☑ | ☑ | ☑ | ☑ |
| Tux3 | ☑ | ☑ | ☑ | ☑ | ☑ | ☑ |
| Ubifs | ☑ | ☑ | ☑ | ☑ | ☑ | ☑ |
| Udf | ☑ | ☑ | ☑ | ☑ | ☑ | ☑ |
| Ufs | ☑ | ☑ | ☑ | ☑ | ☑ | ☑ |
| Vdfs | ☑ | ☑ | ☑ | ☑ | ☑ | ☑ |
| VxFs | ☑ | ☑ | ☐ | ☐ | ☑ | ☐ |
| Wafl | ☐ | ☐ | ☐ | ☐ | ☐ | ☐ |
| Xenix | ☑ | ☑ | ☑ | ☑ | ☑ | ☑ |
| Xfs | ☑ | ☑ | ☑ | ☑ | ☑ | ☑ |
| Yaffs2 | ☑ | ☑ | ☑ | ☑ | ☑ | ☑ |
| Zfs | ☑ | ☑ | ☑ | ☑ | ☐ | ☑ |
| ZxScl | ☑ | ☑ | ☑ | ☑ | ☑ | ☐ |

## Archive / stream descriptors with at least one operation

This table is intentionally exhaustive for descriptors that expose at least one maintenance operation; there is no hidden “~175 defrag-only formats” bucket. Archive **defrag** includes verified repack/relayout. **Optimize** includes layout tuning and finite compression/dictionary/solid-block parameter search; candidates only win after round-trip verification.

| Format | Compact | Defrag | Shrink | Purge | Wipe | Optimize | Meta reorder |
| --- | :---: | :---: | :---: | :---: | :---: | :---: | :---: |
| Aac | ☑ | ☐ | ☐ | ☐ | ☐ | ☐ | ☐ |
| Ace | ☑ | ☑ | ☐ | ☑ | ☑ | ☐ | ☐ |
| AcronisTib | ☐ | ☐ | ☐ | ☑ | ☐ | ☐ | ☐ |
| Adx | ☑ | ☐ | ☐ | ☐ | ☐ | ☐ | ☐ |
| Afs | ☑ | ☑ | ☐ | ☑ | ☑ | ☐ | ☐ |
| Aica | ☑ | ☐ | ☐ | ☐ | ☐ | ☐ | ☐ |
| Aiff | ☑ | ☐ | ☐ | ☐ | ☐ | ☐ | ☐ |
| Akb | ☑ | ☑ | ☐ | ☐ | ☑ | ☐ | ☐ |
| AlZip | ☑ | ☑ | ☐ | ☑ | ☑ | ☐ | ☐ |
| Ampk | ☑ | ☑ | ☐ | ☑ | ☑ | ☐ | ☐ |
| AndroidBundle | ☑ | ☑ | ☐ | ☑ | ☑ | ☐ | ☐ |
| AndroidOta | ☑ | ☐ | ☐ | ☐ | ☐ | ☐ | ☐ |
| AndroidSparse | ☑ | ☐ | ☐ | ☐ | ☐ | ☐ | ☐ |
| Ani | ☑ | ☐ | ☐ | ☐ | ☐ | ☐ | ☐ |
| Aomei | ☑ | ☐ | ☐ | ☑ | ☐ | ☐ | ☐ |
| Apk | ☑ | ☑ | ☐ | ☑ | ☑ | ☐ | ☐ |
| ApkNativeLibs | ☑ | ☐ | ☐ | ☐ | ☑ | ☐ | ☐ |
| AppImage | ☑ | ☐ | ☐ | ☐ | ☐ | ☐ | ☐ |
| AppleSingle | ☑ | ☐ | ☐ | ☑ | ☐ | ☐ | ☐ |
| Appx | ☑ | ☑ | ☐ | ☑ | ☑ | ☐ | ☐ |
| Ar | ☑ | ☑ | ☐ | ☑ | ☑ | ☐ | ☐ |
| Arc | ☑ | ☑ | ☐ | ☑ | ☑ | ☐ | ☐ |
| Arj | ☑ | ☑ | ☐ | ☑ | ☑ | ☐ | ☐ |
| Asar | ☑ | ☐ | ☐ | ☐ | ☐ | ☐ | ☐ |
| Ast | ☑ | ☐ | ☐ | ☐ | ☐ | ☐ | ☐ |
| Au | ☑ | ☐ | ☐ | ☐ | ☐ | ☐ | ☐ |
| Aud | ☑ | ☐ | ☐ | ☐ | ☐ | ☐ | ☐ |
| Avi | ☐ | ☐ | ☐ | ☐ | ☐ | ☐ | ☑ |
| Avr | ☑ | ☐ | ☐ | ☐ | ☐ | ☐ | ☐ |
| Awb | ☑ | ☑ | ☐ | ☐ | ☑ | ☐ | ☐ |
| Ba2 | ☑ | ☑ | ☐ | ☑ | ☑ | ☐ | ☐ |
| Bcstm | ☑ | ☐ | ☐ | ☐ | ☐ | ☐ | ☐ |
| Bfstm | ☑ | ☐ | ☐ | ☐ | ☐ | ☐ | ☐ |
| Big | ☑ | ☑ | ☐ | ☑ | ☑ | ☐ | ☐ |
| BinaryII | ☑ | ☑ | ☐ | ☑ | ☑ | ☐ | ☐ |
| BinCue | ☑ | ☑ | ☐ | ☑ | ☑ | ☐ | ☐ |
| Bkf | ☑ | ☐ | ☐ | ☑ | ☐ | ☐ | ☐ |
| Bonk | ☑ | ☐ | ☐ | ☐ | ☐ | ☐ | ☐ |
| Brotli | ☑ | ☐ | ☐ | ☐ | ☐ | ☑ | ☐ |
| Brr | ☑ | ☐ | ☐ | ☐ | ☐ | ☐ | ☐ |
| Brstm | ☑ | ☐ | ☐ | ☐ | ☐ | ☐ | ☐ |
| Bsa | ☑ | ☑ | ☐ | ☑ | ☑ | ☐ | ☐ |
| Bwav | ☑ | ☐ | ☐ | ☐ | ☐ | ☐ | ☐ |
| Bzip2 | ☑ | ☐ | ☐ | ☐ | ☐ | ☑ | ☐ |
| Cab | ☑ | ☑ | ☐ | ☑ | ☑ | ☐ | ☐ |
| Caf | ☑ | ☐ | ☐ | ☐ | ☐ | ☐ | ☐ |
| Cb7 | ☑ | ☑ | ☐ | ☑ | ☑ | ☐ | ☐ |
| Cbr | ☑ | ☑ | ☐ | ☑ | ☑ | ☐ | ☐ |
| Cbz | ☑ | ☑ | ☐ | ☑ | ☑ | ☐ | ☐ |
| Cdi | ☑ | ☑ | ☐ | ☑ | ☐ | ☐ | ☐ |
| Chm | ☑ | ☑ | ☐ | ☑ | ☑ | ☐ | ☐ |
| CompactPro | ☑ | ☑ | ☐ | ☑ | ☑ | ☐ | ☐ |
| Cpio | ☑ | ☑ | ☐ | ☑ | ☑ | ☐ | ☐ |
| Crate | ☑ | ☐ | ☐ | ☐ | ☐ | ☐ | ☐ |
| Crx | ☑ | ☑ | ☐ | ☐ | ☑ | ☐ | ☐ |
| Cso | ☑ | ☐ | ☐ | ☑ | ☐ | ☐ | ☐ |
| Cur | ☑ | ☑ | ☐ | ☐ | ☐ | ☐ | ☐ |
| Cvsd | ☑ | ☐ | ☐ | ☐ | ☐ | ☐ | ☐ |
| Dcs | ☑ | ☑ | ☐ | ☐ | ☐ | ☐ | ☐ |
| Deb | ☑ | ☑ | ☐ | ☑ | ☑ | ☐ | ☐ |
| Dff | ☑ | ☐ | ☐ | ☐ | ☐ | ☐ | ☐ |
| Dfpwm | ☑ | ☐ | ☐ | ☐ | ☐ | ☐ | ☐ |
| DiskDoubler | ☑ | ☑ | ☐ | ☐ | ☑ | ☐ | ☐ |
| Dmg | ☑ | ☑ | ☐ | ☑ | ☐ | ☐ | ☐ |
| Dms | ☑ | ☑ | ☐ | ☐ | ☐ | ☐ | ☐ |
| Doc | ☑ | ☑ | ☐ | ☑ | ☑ | ☐ | ☐ |
| Docx | ☑ | ☑ | ☐ | ☑ | ☑ | ☐ | ☐ |
| Dsf | ☑ | ☐ | ☐ | ☐ | ☐ | ☐ | ☐ |
| Dtb | ☑ | ☐ | ☐ | ☑ | ☐ | ☐ | ☐ |
| Dzip | ☑ | ☑ | ☐ | ☑ | ☑ | ☐ | ☐ |
| Ear | ☑ | ☑ | ☐ | ☑ | ☑ | ☐ | ☐ |
| EaSchl | ☑ | ☐ | ☐ | ☐ | ☐ | ☐ | ☐ |
| Egg | ☑ | ☑ | ☐ | ☑ | ☐ | ☐ | ☐ |
| Eml | ☑ | ☐ | ☐ | ☑ | ☐ | ☐ | ☐ |
| Epub | ☑ | ☑ | ☐ | ☑ | ☑ | ☐ | ☐ |
| Esd | ☑ | ☐ | ☐ | ☐ | ☑ | ☐ | ☐ |
| Ewf | ☑ | ☑ | ☑ | ☑ | ☑ | ☑ | ☐ |
| Fits | ☑ | ☑ | ☐ | ☐ | ☑ | ☐ | ☐ |
| Fla | ☐ | ☐ | ☐ | ☐ | ☑ | ☐ | ☐ |
| Flac | ☑ | ☐ | ☐ | ☐ | ☑ | ☐ | ☐ |
| FreeArc | ☑ | ☑ | ☐ | ☑ | ☑ | ☐ | ☐ |
| GameMaker | ☑ | ☐ | ☐ | ☐ | ☐ | ☐ | ☐ |
| Gar | ☑ | ☑ | ☐ | ☑ | ☑ | ☐ | ☐ |
| Gem | ☑ | ☐ | ☐ | ☐ | ☐ | ☐ | ☐ |
| Ghost | ☑ | ☐ | ☐ | ☑ | ☐ | ☐ | ☐ |
| Gif | ☐ | ☐ | ☐ | ☐ | ☐ | ☐ | ☑ |
| Gob | ☑ | ☑ | ☐ | ☑ | ☑ | ☐ | ☐ |
| GodotPck | ☑ | ☑ | ☐ | ☑ | ☑ | ☐ | ☐ |
| Grp | ☑ | ☑ | ☐ | ☑ | ☑ | ☐ | ☐ |
| Gzip | ☑ | ☐ | ☐ | ☐ | ☐ | ☑ | ☐ |
| Ha | ☑ | ☑ | ☐ | ☑ | ☑ | ☐ | ☐ |
| Hcom | ☑ | ☐ | ☐ | ☐ | ☐ | ☐ | ☐ |
| Hog | ☑ | ☑ | ☐ | ☑ | ☑ | ☐ | ☐ |
| Hpi | ☑ | ☑ | ☐ | ☑ | ☑ | ☐ | ☐ |
| Hps | ☑ | ☐ | ☐ | ☐ | ☐ | ☐ | ☐ |
| Ico | ☑ | ☑ | ☐ | ☑ | ☐ | ☐ | ☐ |
| IffCdaf | ☑ | ☑ | ☐ | ☑ | ☑ | ☐ | ☐ |
| InnoSetup | ☑ | ☑ | ☐ | ☐ | ☐ | ☐ | ☐ |
| Ipa | ☑ | ☑ | ☐ | ☑ | ☑ | ☐ | ☐ |
| Ipsw | ☑ | ☐ | ☐ | ☑ | ☑ | ☐ | ☐ |
| Ircam | ☑ | ☐ | ☐ | ☐ | ☐ | ☐ | ☐ |
| Jar | ☑ | ☑ | ☐ | ☑ | ☑ | ☐ | ☐ |
| Kmz | ☑ | ☑ | ☐ | ☑ | ☑ | ☐ | ☐ |
| Lbr | ☑ | ☑ | ☐ | ☑ | ☑ | ☐ | ☐ |
| Lfd | ☑ | ☑ | ☐ | ☐ | ☑ | ☐ | ☐ |
| LhF | ☑ | ☑ | ☐ | ☑ | ☑ | ☐ | ☐ |
| Lnk | ☑ | ☐ | ☐ | ☐ | ☐ | ☐ | ☐ |
| Lpc10 | ☑ | ☐ | ☐ | ☐ | ☐ | ☐ | ☐ |
| Lrzip | ☑ | ☑ | ☐ | ☐ | ☐ | ☐ | ☐ |
| Lynx | ☑ | ☑ | ☐ | ☑ | ☑ | ☐ | ☐ |
| Lz4 | ☑ | ☐ | ☐ | ☐ | ☐ | ☑ | ☐ |
| Lzh | ☑ | ☑ | ☐ | ☑ | ☑ | ☐ | ☐ |
| Lzip | ☑ | ☐ | ☐ | ☐ | ☐ | ☑ | ☐ |
| Lzma | ☑ | ☐ | ☐ | ☐ | ☐ | ☑ | ☐ |
| LzxAmiga | ☑ | ☑ | ☐ | ☑ | ☑ | ☐ | ☐ |
| Macrium | ☑ | ☐ | ☐ | ☐ | ☐ | ☐ | ☐ |
| Maff | ☑ | ☑ | ☐ | ☑ | ☑ | ☐ | ☐ |
| Maud | ☑ | ☐ | ☐ | ☐ | ☐ | ☐ | ☐ |
| Mbox | ☑ | ☐ | ☐ | ☑ | ☐ | ☐ | ☐ |
| Mdf | ☑ | ☑ | ☐ | ☑ | ☐ | ☐ | ☐ |
| Mhk | ☑ | ☑ | ☐ | ☐ | ☑ | ☐ | ☐ |
| Midi | ☑ | ☐ | ☐ | ☐ | ☐ | ☐ | ☐ |
| Mix | ☑ | ☑ | ☐ | ☐ | ☑ | ☐ | ☐ |
| Mkv | ☐ | ☐ | ☐ | ☐ | ☐ | ☐ | ☑ |
| Mo | ☑ | ☐ | ☐ | ☐ | ☐ | ☐ | ☐ |
| Mp3 | ☑ | ☑ | ☐ | ☐ | ☐ | ☐ | ☑ |
| Mp4 | ☐ | ☐ | ☐ | ☐ | ☐ | ☐ | ☑ |
| Mpq | ☑ | ☑ | ☐ | ☑ | ☑ | ☐ | ☐ |
| Msg | ☑ | ☑ | ☐ | ☑ | ☑ | ☐ | ☐ |
| Msi | ☑ | ☑ | ☐ | ☑ | ☑ | ☐ | ☐ |
| Msix | ☑ | ☑ | ☐ | ☑ | ☑ | ☐ | ☐ |
| Narc | ☑ | ☑ | ☐ | ☑ | ☑ | ☐ | ☐ |
| Nds | ☑ | ☑ | ☐ | ☑ | ☑ | ☐ | ☐ |
| Npy | ☑ | ☐ | ☐ | ☐ | ☐ | ☐ | ☐ |
| Npz | ☑ | ☐ | ☐ | ☐ | ☑ | ☐ | ☐ |
| Nrg | ☑ | ☑ | ☐ | ☑ | ☐ | ☐ | ☐ |
| Nsa | ☑ | ☑ | ☐ | ☑ | ☑ | ☐ | ☐ |
| Nsis | ☑ | ☑ | ☐ | ☐ | ☐ | ☐ | ☐ |
| NuFx | ☑ | ☑ | ☑ | ☑ | ☑ | ☐ | ☐ |
| NuPkg | ☑ | ☑ | ☐ | ☑ | ☑ | ☐ | ☐ |
| Odp | ☑ | ☑ | ☐ | ☑ | ☑ | ☐ | ☐ |
| Ods | ☑ | ☑ | ☐ | ☑ | ☑ | ☐ | ☐ |
| Odt | ☑ | ☑ | ☐ | ☑ | ☑ | ☐ | ☐ |
| Ogg | ☑ | ☐ | ☐ | ☐ | ☐ | ☐ | ☑ |
| Ova | ☑ | ☐ | ☐ | ☑ | ☐ | ☐ | ☐ |
| PackDisk | ☑ | ☑ | ☐ | ☐ | ☐ | ☐ | ☐ |
| PackIt | ☑ | ☑ | ☐ | ☑ | ☑ | ☐ | ☐ |
| Paf | ☑ | ☐ | ☐ | ☐ | ☐ | ☐ | ☐ |
| Pak | ☑ | ☑ | ☐ | ☑ | ☑ | ☐ | ☐ |
| Paragon | ☑ | ☐ | ☐ | ☑ | ☐ | ☐ | ☐ |
| Pbp | ☑ | ☑ | ☐ | ☐ | ☑ | ☐ | ☐ |
| Pdf | ☑ | ☑ | ☐ | ☑ | ☑ | ☐ | ☐ |
| Pfs0 | ☑ | ☑ | ☐ | ☑ | ☑ | ☐ | ☐ |
| Png | ☐ | ☐ | ☐ | ☐ | ☐ | ☐ | ☑ |
| Ppt | ☑ | ☑ | ☐ | ☑ | ☑ | ☐ | ☐ |
| Pptx | ☑ | ☑ | ☐ | ☑ | ☑ | ☐ | ☐ |
| Psarc | ☑ | ☑ | ☐ | ☑ | ☑ | ☐ | ☐ |
| Psf | ☑ | ☑ | ☐ | ☐ | ☐ | ☐ | ☐ |
| Pvf | ☑ | ☐ | ☐ | ☐ | ☐ | ☐ | ☐ |
| Qcow2 | ☑ | ☑ | ☐ | ☑ | ☑ | ☐ | ☐ |
| Qoa | ☑ | ☐ | ☐ | ☐ | ☐ | ☐ | ☐ |
| Qoi | ☑ | ☐ | ☐ | ☐ | ☐ | ☐ | ☐ |
| Rar | ☑ | ☑ | ☐ | ☑ | ☑ | ☐ | ☐ |
| Rarc | ☑ | ☑ | ☐ | ☑ | ☑ | ☐ | ☐ |
| ResourceDll | ☑ | ☑ | ☐ | ☐ | ☐ | ☐ | ☐ |
| Rf64 | ☑ | ☐ | ☐ | ☐ | ☐ | ☐ | ☐ |
| Rgss | ☑ | ☑ | ☐ | ☑ | ☑ | ☐ | ☐ |
| Roq | ☑ | ☐ | ☐ | ☐ | ☐ | ☐ | ☐ |
| Rpa | ☑ | ☑ | ☐ | ☑ | ☑ | ☐ | ☐ |
| Rpm | ☑ | ☑ | ☐ | ☐ | ☑ | ☐ | ☐ |
| Sar | ☑ | ☑ | ☐ | ☑ | ☑ | ☐ | ☐ |
| Sarc | ☑ | ☑ | ☐ | ☑ | ☑ | ☐ | ☐ |
| SevenZip | ☑ | ☑ | ☐ | ☑ | ☑ | ☑ | ☐ |
| Sfar | ☑ | ☐ | ☐ | ☐ | ☑ | ☐ | ☐ |
| Shar | ☑ | ☑ | ☐ | ☑ | ☐ | ☐ | ☐ |
| Shn | ☑ | ☐ | ☐ | ☐ | ☐ | ☐ | ☐ |
| Sketch | ☐ | ☐ | ☐ | ☐ | ☑ | ☐ | ☐ |
| Slf | ☑ | ☑ | ☐ | ☑ | ☑ | ☐ | ☐ |
| Smp | ☑ | ☐ | ☐ | ☐ | ☐ | ☐ | ☐ |
| Snap | ☑ | ☐ | ☐ | ☐ | ☐ | ☐ | ☐ |
| Sndr | ☑ | ☐ | ☐ | ☐ | ☐ | ☐ | ☐ |
| Sndt | ☑ | ☐ | ☐ | ☐ | ☐ | ☐ | ☐ |
| Sol | ☑ | ☐ | ☐ | ☐ | ☐ | ☐ | ☐ |
| Spark | ☑ | ☑ | ☐ | ☑ | ☑ | ☐ | ☐ |
| Sparseimage | ☑ | ☐ | ☐ | ☑ | ☐ | ☐ | ☐ |
| Sphere | ☑ | ☐ | ☐ | ☐ | ☐ | ☐ | ☐ |
| SplitFile | ☑ | ☑ | ☐ | ☐ | ☐ | ☐ | ☐ |
| Sqx | ☑ | ☑ | ☐ | ☑ | ☑ | ☐ | ☐ |
| Srec | ☑ | ☐ | ☐ | ☐ | ☐ | ☐ | ☐ |
| StuffIt | ☑ | ☑ | ☐ | ☑ | ☑ | ☐ | ☐ |
| StuffItX | ☑ | ☑ | ☐ | ☐ | ☑ | ☐ | ☐ |
| Svx8 | ☑ | ☐ | ☐ | ☐ | ☐ | ☐ | ☐ |
| Swav | ☑ | ☐ | ☐ | ☐ | ☐ | ☐ | ☐ |
| Swm | ☐ | ☐ | ☐ | ☐ | ☑ | ☐ | ☐ |
| T64 | ☑ | ☑ | ☐ | ☑ | ☑ | ☐ | ☐ |
| Tap | ☑ | ☑ | ☐ | ☑ | ☑ | ☐ | ☐ |
| Tar | ☑ | ☑ | ☑ | ☑ | ☑ | ☑ | ☐ |
| Tfc | ☑ | ☑ | ☐ | ☐ | ☐ | ☐ | ☐ |
| TfRecord | ☑ | ☑ | ☐ | ☐ | ☑ | ☐ | ☐ |
| ThumbsDb | ☑ | ☑ | ☐ | ☑ | ☑ | ☐ | ☐ |
| Tiff | ☐ | ☐ | ☐ | ☐ | ☐ | ☐ | ☑ |
| Tnef | ☑ | ☑ | ☐ | ☑ | ☐ | ☐ | ☐ |
| Tta | ☑ | ☐ | ☐ | ☐ | ☐ | ☐ | ☐ |
| Ttc | ☑ | ☐ | ☐ | ☐ | ☐ | ☐ | ☐ |
| Txw | ☑ | ☐ | ☐ | ☐ | ☐ | ☐ | ☐ |
| U8 | ☑ | ☑ | ☐ | ☑ | ☑ | ☐ | ☐ |
| UefiFv | ☑ | ☐ | ☐ | ☑ | ☐ | ☐ | ☐ |
| Uharc | ☑ | ☑ | ☐ | ☑ | ☑ | ☐ | ☐ |
| Umx | ☑ | ☑ | ☐ | ☐ | ☑ | ☐ | ☐ |
| UnityBundle | ☑ | ☑ | ☐ | ☑ | ☐ | ☑ | ☐ |
| UnrealPak | ☑ | ☑ | ☐ | ☑ | ☐ | ☐ | ☐ |
| Vag | ☑ | ☐ | ☐ | ☐ | ☐ | ☐ | ☐ |
| Vdi | ☑ | ☑ | ☐ | ☑ | ☑ | ☐ | ☐ |
| Vhd | ☑ | ☑ | ☐ | ☑ | ☑ | ☐ | ☐ |
| Vhdx | ☑ | ☑ | ☐ | ☑ | ☑ | ☐ | ☐ |
| Vib | ☑ | ☐ | ☐ | ☐ | ☐ | ☑ | ☐ |
| Vmdk | ☑ | ☑ | ☐ | ☑ | ☑ | ☐ | ☐ |
| Voc | ☑ | ☐ | ☐ | ☐ | ☐ | ☐ | ☐ |
| Vox | ☑ | ☐ | ☐ | ☐ | ☐ | ☐ | ☐ |
| Vpk | ☑ | ☑ | ☐ | ☑ | ☑ | ☐ | ☐ |
| Vpp | ☑ | ☑ | ☐ | ☑ | ☑ | ☐ | ☐ |
| VppV2 | ☑ | ☑ | ☐ | ☑ | ☑ | ☐ | ☐ |
| Vsdx | ☑ | ☑ | ☐ | ☑ | ☑ | ☐ | ☐ |
| Wacz | ☑ | ☐ | ☐ | ☐ | ☑ | ☐ | ☐ |
| Wad | ☑ | ☑ | ☐ | ☑ | ☑ | ☐ | ☐ |
| Wad2 | ☑ | ☑ | ☐ | ☑ | ☑ | ☐ | ☐ |
| War | ☑ | ☑ | ☐ | ☑ | ☑ | ☐ | ☐ |
| Warc | ☑ | ☑ | ☐ | ☐ | ☑ | ☐ | ☐ |
| Wav | ☑ | ☑ | ☐ | ☐ | ☐ | ☐ | ☑ |
| Wave64 | ☑ | ☐ | ☐ | ☐ | ☐ | ☐ | ☐ |
| Wbn | ☑ | ☐ | ☐ | ☐ | ☐ | ☐ | ☐ |
| Wheel | ☑ | ☐ | ☐ | ☐ | ☑ | ☐ | ☐ |
| Wim | ☑ | ☑ | ☐ | ☐ | ☑ | ☐ | ☐ |
| Wrapster | ☑ | ☑ | ☐ | ☑ | ☑ | ☐ | ☐ |
| Xa | ☑ | ☐ | ☐ | ☐ | ☐ | ☐ | ☐ |
| Xar | ☑ | ☑ | ☐ | ☑ | ☑ | ☐ | ☐ |
| xDisk | ☑ | ☑ | ☐ | ☐ | ☐ | ☐ | ☐ |
| Xls | ☑ | ☑ | ☐ | ☑ | ☑ | ☐ | ☐ |
| Xlsx | ☑ | ☑ | ☐ | ☑ | ☑ | ☐ | ☐ |
| xMash | ☑ | ☑ | ☐ | ☐ | ☐ | ☐ | ☐ |
| Xpi | ☑ | ☑ | ☐ | ☑ | ☑ | ☐ | ☐ |
| Xps | ☑ | ☑ | ☐ | ☑ | ☑ | ☐ | ☐ |
| Xz | ☑ | ☐ | ☐ | ☐ | ☐ | ☑ | ☐ |
| Ypf | ☑ | ☑ | ☐ | ☑ | ☑ | ☐ | ☐ |
| Zap | ☑ | ☑ | ☐ | ☐ | ☑ | ☐ | ☐ |
| Zip | ☑ | ☑ | ☑ | ☑ | ☑ | ☑ | ☐ |
| Zlib | ☑ | ☐ | ☐ | ☐ | ☐ | ☑ | ☐ |
| Zoo | ☑ | ☑ | ☐ | ☑ | ☑ | ☐ | ☐ |
| Zpaq | ☑ | ☑ | ☐ | ☑ | ☐ | ☐ | ☐ |
| Zstd | ☑ | ☐ | ☐ | ☐ | ☐ | ☑ | ☐ |

## N/A notes

- **Shrink on fixed-geometry images** (most read-only filesystems, optical
  images): no smaller canonical size exists, so Shrink is intentionally absent.
- **Metadata-reorder on archives with a single central directory** (Zip, 7z):
  Defragment already lands the central directory in its canonical contiguous
  trailing position, so a separate reorder pass would be a no-op.
- **Cluster-tip wiping on solid/streamed containers** (7z, Tar): there is no
  per-file cluster slack — the wiper zeros inter-block gaps and trailing junk
  only.
