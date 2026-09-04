# Maintenance mechanisms and write capability

How the maintenance verbs are provided, and what a read-write claim is allowed
to mean. The verbs themselves — optimize, shrink, defrag, purge, wipe and the
`compact` composite — are defined once in [`ARCHIVE-MODEL.md`](ARCHIVE-MODEL.md)
&rarr; *The five maintenance verbs*, together with the interface that unlocks
each. This page is the half that does not fit a table: why so few formats need
bespoke code for any of it, and the rule that decides when `CanModify` may be
advertised.

Per-format coverage is not here. It is in the package READMEs, rendered from the
descriptors — see [the end of this page](#where-the-per-format-coverage-lives).

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

- **`ILayoutOptimizable`** carries the same kind of default — a verified rebuild
  honouring `LayoutRebuildOptions` geometry — guarded by
  `GenericLayoutOptimizableTests`. That default needs a creator to write the new
  volume with, so declaring the interface is not by itself a re-lay: a descriptor
  may implement it purely to publish its geometry analysis, as ReFS does. The
  Layout column of the support matrix reports the rebuild rather than the
  interface, and is the count of how far this reaches.
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

Write capability is the four-level scale of `Compression.Registry.FormatCapabilities`
— unsupported, read-only, WORM, R/W — tabulated in
[`ARCHIVE-MODEL.md`](ARCHIVE-MODEL.md) &rarr; *Read / WORM / Read-Write model*.
What follows is the part that decides which level a descriptor may claim.

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

## Where the per-format coverage lives

A support table belongs to the package that ships the code it describes, so
there is no coverage matrix on this page.

- **Filesystems and disk-image containers** — the support matrix in
  [`Hawkynt.FileFormats.FileSystems/README.md`](../Hawkynt.FileFormats.FileSystems/README.md).
  Its Compact, Defrag, Wipe, Shrink, Layout and Purge columns are rendered from
  the descriptors by `Compression.Tests/Documentation/FilesystemSupportMatrix.cs`
  and re-derived on every build, so a cell that stops matching the code fails
  rather than misleading a reader.
- **Archives** — the *Maintenance* column of
  [`Hawkynt.FileFormats.Archives/README.md`](../Hawkynt.FileFormats.Archives/README.md).
- **Whatever is loaded right now** — `cwb formats`, which answers from the live
  registry and is the authority both tables are checked against.
