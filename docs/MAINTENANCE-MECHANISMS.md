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
  `Remove(all)` is the **purge** verb. Two things the purge has to know about the
  container it is emptying, because neither is a defect of the verb:
  - **Rendered entries.** A reader may publish views of the container itself — a
    whole-image entry, a metadata rendering, a raw superblock or log dump, an index
    the format keeps for its own use. Asking the modifier to drop one is meaningless
    and finding one afterwards proves nothing. They are told apart from user data by
    what a descriptor declares through `ISyntheticEntryNames` plus what an empty
    container of the same format still lists, and that reference container is built
    only once a plain attempt has tripped over one.
  - **A narrower native namespace.** A descriptor's own modifier may address sectors
    or blocks where its reader lists files (BIN/CUE, CDI, MDF, NRG, CSO). The verb is
    still reachable there through the same extract → drop → re-create rebuild, which
    is tried before a purge is reported impossible.

  **`IArchivePurgeable.CanPurgeToEmpty`** is the one honest way out: `false` says the
  container mandates at least one member, so there is no empty instance for a purge to
  leave behind — a ZPAQ needs a block, an OVA a disk or descriptor, a Wrapster its
  payload, an NDS ROM a NitroFS. Those still add and remove individual entries; only
  the empty end state does not exist, and the verb says so instead of writing
  something its own reader rejects.

**`IFilesystemScrambleable.Scramble`** is the exception that proves the pattern: it
has no default and no rebuild behind it. A rebuild lays a volume out contiguously,
which is the opposite of what the verb asks for, so a descriptor that cannot scatter
in place refuses and names what stopped it rather than reporting success. It exists
so the defragmenter can be tested against a volume that is genuinely fragmented —
nothing else in the public surface produces one.

**`IFilesystemPlaceable.PlaceFileAt`** follows scramble's precedent for the same
reason. It takes two things no defragmentation takes — which owner, and where — so a
`DefragMode` carrying them would be an operation reachable by a mis-set enum value on
a method whose name means something else. It shares *carve-hole*'s eviction rather
than repeating it: carving clears a region and leaves it empty, placement clears the
same way and then lays the owner down there. There is no rebuild behind it either —
a rebuild lays the volume out in directory order, which is not the order that was
asked for.

**Ascending order** (`DefragMode.AscendingOrder`) is the weaker goal both verbs
promise: over an owner's own blocks in logical order, `block(n) > block(n-1)`, so a
sequential read never seeks backwards. `AscendingBlockOrder` states it as a checkable
property rather than a comment, and the fixtures assert it after a placement and after
an ordinary defragmentation — a partial success is only worth having if the pieces are
in the right order. It was expected to be a way around movers that lack
`SupportsHeldRuns`; measured, it is not. It needs holding *more* often than packing
does, because packing vacates space as it sweeps forward while sorting an owner in
place has nothing spare. What it buys is cost: about a third of the bytes, because it
touches only the blocks that are actually out of order.

A filesystem descriptor therefore gains shrink / defrag / purge by simply declaring
the interface (it already implements `IArchiveFormatOperations` + `IArchiveCreatable`).
Bespoke in-place implementations still override the default for efficiency. Coverage
is guarded by the registry-parametrised `Generic{Shrink,Defrag,Purge}RoundTripTests`
under `Compression.Tests/Operations/`. For every creatable claimant they build the
same conservative one-payload probe, invoke the advertised verb, and identify that
payload through the descriptor's own entry reader by SHA-256 rather than by filename,
so single-stream and entry-renaming formats are exercised too. `NotSupportedException`,
another runtime refusal, an unreadable result, or a dropped entry is a test failure —
an advertised operation is not allowed to turn refusal into green CI.

Whether the reader can hand the planted payload back *at all* is a property of the
create/read path rather than of the verb, so the probe establishes it before the verb
runs instead of assuming it. A container that rasterises files into disk tracks,
transcodes them, or re-frames them as a message cannot return them verbatim; it is
still held to executing the verb and to keeping every entry its reader listed, and the
byte-for-byte clause applies wherever the payload was retrievable to begin with. The
only way a format leaves the suite entirely is by declaring, through
`IArchiveWriteConstraints`, that the probe payload is not a legal member — an
undeclared create refusal fails.

- **`ILayoutOptimizable`** carries the same kind of default — a verified rebuild
  honouring `LayoutRebuildOptions` geometry — guarded by
  `GenericLayoutOptimizableTests`. Creatable claimants must successfully analyse and
  rebuild the standard probe, with byte-identical payloads afterwards. That default
  needs a creator to write the new volume with, so declaring the interface is not by
  itself a re-lay: a descriptor may implement it purely to publish its geometry
  analysis, as ReFS does. The Layout column of the support matrix reports the rebuild
  rather than the interface, and is the count of how far this reaches.
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
no unbacked flag). `FilesystemWriteRoundTripTests` then exercises create → add/replace → remove
across every filesystem that claims R/W, and the per-format mutation suites cover the rest;
the generic purge contract independently proves that a claimant advertising
`IArchivePurgeable` can actually remove the planted live files and leave a valid container.

### R/W realisation per format

- **Byte-preserving in place** (existing data stays put): FAT12/16/32 (`FatModifier`), GEMDOS,
  GS/OS, exFAT, ext, HFS/HFS+, APFS, F2FS, JFS, UFS, UDF, the log-structured
  JFFS2/YAFFS2/UBIFS/NILFS2, the CVF family, the retro disk formats; PS1 memory-card
  deletion (which marks directory records deleted while retaining recoverable save blocks);
  the in-place archive editors (ZIP family, TAR, AR, CPIO, XAR, LZH/LHA, ARJ, ZOO, PDF);
  byte-identity append (Ghost); the sector-image editors (BIN/CUE, CDI, MDF, NRG, CSO); and
  the disk-image containers that delegate to a R/W inner filesystem (QCOW2/VHD/VHDX/VMDK/VDI).
- **Relayout / re-pack** (valid result, existing data may move): **NTFS, XFS, Btrfs, ReiserFS,
  GFS2, MFS-1, Stacker, CramFS, SquashFS, EROFS** and PS1 memory-card add/replace/defrag
  (the supported image is rebuilt or re-packed and verified), plus **7-Zip, CAB, RAR**
  (the solid streams are rewritten via the extract → re-create rebuild; RAR re-emits a valid
  RAR5 via `RarWriter` and recomputes every CRC — so the cross-referencing-checksum concern
  of an append-style edit does not apply). GFS2 preserves the existing image-size floor and
  lock-table value across CRUD; Stacker preserves Genuine vs Extended layout; MFS-1 preserves
  the outer sector count.

### Stays WORM (create-only)

- **Wim**, **Swm** — checksum-record archives kept create-only: there is no in-place
  editor, and an append-style edit would corrupt the cross-referencing checksum chain
  (see `ChecksumRecordArchiveReadOnlyContractTests`). Sqx and Ace belong to the same
  family but do carry an existing-instance editor — the verified extract → edit →
  re-create rebuild — and so advertise R/W; the checksum chain is re-derived rather
  than appended to.
- **Wrapster**, **Ova** remain create-only because their public writer profile does not
  provide an arbitrary existing-instance member edit that satisfies the generic CRUD
  contract. A rebuild by itself is not a reason to withhold R/W; absence of a proven
  edit path is.

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
