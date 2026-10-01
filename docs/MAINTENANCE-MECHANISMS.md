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
  isn't smaller or fails). Filesystems with metadata the rebuild cannot carry
  (ext, FAT, NTFS, …) override it with an in-place trimmer or do not offer it.
- **`IArchiveDefragmentable.Defragment`** — default verified in-place rebuild, for
  containers whose rebuild is lossless. The major filesystems defragment in place
  through the planner and refuse what it cannot lay out.
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

**R/W means a working modify on an existing container that keeps everything the format
carries.** The edit may be byte-preserving in place or may relayout the container (moving
existing data) — but a relayout is honest R/W only when it is *lossless*: names, contents,
directories (empty ones too), owners, modes/attributes, timestamps, symlinks and hard links,
extended attributes, the volume label / serial / UUID, the compression choice and the volume's
size. A rebuild that quietly drops any of that is WORM pretending to be R/W, and a self
round-trip cannot see it — our own writer never produced those things in the first place.

> **`CanModify` is advertised only when the existing-instance edit path is proven lossless
> against a container the real tools made.** An operating system mounting a filesystem
> read-only does not make an offline image editor WORM, but neither does having a rebuild make
> it R/W. Where a case cannot be done without loss the verb refuses with
> `NotSupportedException` and leaves the container byte for byte as it was — the same answer
> the maintenance window and the CLI already treat as "this format does not do that".

The same rule applies to the maintenance verbs. **Defragment** keeps the image size and every
byte and attribute of every file; a layout the in-place planner cannot reach is refused
(`DefragPlanner.PlanOrRefuse`, `DefragContentGuard` with a refusing fallback), and a layout
option the format would otherwise ignore — interleave, metadata placement, a layout template,
a mode it does not implement — is refused up front by `DefragSupport.Require` rather than
silently dropped. **Shrink** trims in place or is not advertised. **Wipe** zeroes only what the
layout map proves free; every map reports every allocated byte (a directory index, a named
stream, a continuation area, a boot image, data past the volume) or reserves what its
allocation bitmap marks in use and nothing claimed.

`Compression.Tests.Operations.WriteCapabilityHonestyTests` enforces the deterministic half:
every `CanModify` claimant's ops must implement `IArchiveModifiable`. The evidence half is
`Compression.Tests.Maintenance.MaintenancePreservesRealVolumesTests`: it formats reference
volumes with `mkfs.*`, fills them through the kernel driver (libguestfs), runs every verb and
compares a manifest read back through the same driver — names, types, modes, owners, sizes,
times, link targets and counts, xattrs, digests, label, UUID, and DOS attributes via `mattrib`
— and runs the reference checker. Its table is the evidence matrix below.

### Evidence matrix (reference volumes made by the real tools)

`P` = in place, nothing else changed, reference checker clean. `R` = refused, image
untouched. Wipe and shrink are in place; defrag keeps the image size.

| Format (tool) | add root | add nested | remove root | remove nested | pack start / end / fill | carve | ascending | interleave | metadata front | wipe | shrink | checker |
|---|---|---|---|---|---|---|---|---|---|---|---|---|
| ext4, ext2 (`mkfs.ext*`) | P | P | P | P | P | P | P | R | P | P | P | `e2fsck -fn` |
| FAT16, FAT32 (`mkfs.vfat` + `mtools`) | P | P | P | P | P | P | P | P (FAT16) / R (FAT32: move budget) | P | P | P | `fsck.vfat -n` |
| NTFS (`mkfs.ntfs`) | P | P | P | P | P | P | P | R | P | P | P | `ntfsfix -n` |
| exFAT (`mkfs.exfat`) | P | R | P | R | P | P | R | R | P | P | not offered | `fsck.exfat -n` |
| ISO 9660 + RR + Joliet (`xorriso`) | P | R | P | R | P | R when it does not fit | R | R | R | P | not offered | kernel mount, `xorriso` |
| 7z (`7z a -snl`) | P (metadata-preserving rewrite when the in-place adder declines) | P | P | P | — (not offered) | — | — | — | — | P | — | `7z t` |

Demoted (no `CanModify`, no shrink): **XFS**, **Btrfs** (the in-place adders corrupt volumes
made by `mkfs.xfs` / `mkfs.btrfs`; removal only ever existed as a rebuild with a new UUID, no
label, zero timestamps), **SquashFS** (every edit converted the compression to gzip and dropped
owners, modes, times, symlinks and xattrs). Their in-place defrag stays, guarded, and refuses
otherwise. **ZIP, 7z, TAR** no longer offer defragment: an archive has no free-space layout, and
the repack kept only names and bytes.

### R/W realisation per format

- **In place, verified against the real tools' volumes** (see the matrix): ext2/3/4, FAT12/16/32,
  NTFS, exFAT (root directory), ISO 9660 (root directory).
- **In place, verified through the kernel driver on our volumes and on `mkfs.ocfs2 -M local`
  volumes filled through it**: OCFS2 — root-directory files added, replaced and removed, single-run
  files defragmented; `fsck.ocfs2 -fn` clean before and after a kernel read-write mount
  (`Ocfs2KernelMountTests`). Nested paths, extent-backed roots, shared extents and a full volume
  are refused; nothing is rebuilt.
- **In place, checked with the reference checker on a fresh `mkfs.hfsplus` volume only**
  (there is no HFS+ kernel driver to fill one with): HFS+ — root folder of a single-leaf
  catalog; anything else refused.
- **In place, verified against our own writer's output only** (not yet against the real tools'
  volumes): GEMDOS, GS/OS, HFS, APFS, F2FS, JFS, UFS, UDF, JFFS2/YAFFS2/UBIFS/NILFS2, the CVF
  family, the retro disk formats, PS1 memory cards, and the in-place archive editors (ZIP family,
  TAR, AR, CPIO, XAR, LZH/LHA, ARJ, ZOO, PDF), the sector-image editors and the disk-image
  containers that delegate to an inner filesystem.
- **Relayout / re-pack, not yet re-verified for losslessness**: ReiserFS, GFS2, MFS-1, Stacker,
  CramFS, EROFS, CAB, RAR, and every descriptor relying on the default `IArchiveModifiable`
  rebuild. `RebuildVerb.EditViaRebuild` round-trips names and bytes through a temporary folder
  and keeps nothing else, so a format whose container carries more than that is a demotion
  candidate until shown otherwise.

### Stays WORM (create-only)

- **Wim**, **Swm** — checksum-record archives kept create-only: there is no in-place
  editor, and an append-style edit would corrupt the cross-referencing checksum chain
  (see `ChecksumRecordArchiveReadOnlyContractTests`).
- **Wrapster**, **Ova** — the public writer profile provides no existing-instance member edit.
- **XFS**, **Btrfs**, **SquashFS** — see above.

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
