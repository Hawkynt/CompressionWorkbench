# Mounting format families

`FilesystemMountLauncher` resolves a source into `IFilesystemSession` **before**
FUSE3 or Dokan receives it. It never delegates source decoding to the host
filesystem or to an external archive/mount executable.

Path-backed writable mounts can close the source handle and use the file-backed
stager's verified same-directory replacement and recovery journal. Generic stream
mounts retain the qualified native-editor or WORM rebuild path described below;
named writable ZIP streams additionally support the explicit undo-journalled
in-place writer. The distinction and durability limits are described in
[the mounting guide](../Compression.Mounting/FORMAT-MOUNTING.md).

## Resolution order

1. A registered filesystem (`FormatRegistry.FilesystemFormatIds`) uses its
   native descriptor/sidecar driver, or its derived read-only archive view.
2. A registered block-device container (VMDK, etc.) is opened through its own
   managed block provider. A single recognized inner filesystem uses its
   registered filesystem driver. Partitioned images expose one child directory
   per partition. Unsupported/unrecognized partitions expose their exact bytes
   as read-only `PartitionN.raw` entries inside those directories instead of
   preventing the entire mount.
3. A registered archive/pseudo-archive uses `IArchiveFormatOperations` for a
   read-only hierarchy. Real multi-entry archives additionally qualify for
   staged read/write when they expose **either** a safe `IArchiveModifiable`
   editor **or** an `IArchiveCreatable` WORM writer that can faithfully
   recreate their current entries. The latter uses a temporary image copy.
   ZIP, 7z and RAR typically use existing editors; creator-only WIM images can
   qualify via complete reconstruction. The exact source profile must pass
   the checks below.
4. Registered formats with no archive or filesystem interface (such as some
   stream, video, image, executable or media formats) expose a single read-only
   file containing the original bytes. This is a lossless *opaque view*, not an
   invented semantic decoder.
5. Explicit `Raw` or `Unknown` mounting provides the same read-only opaque
   view even when no descriptor exists. Unknown named descriptors are still
   rejected. A `.d90` source without a registered D90 filesystem parser may
   therefore be mounted in `Unknown` mode, but its Commodore directory is
   **not** falsely claimed to be decoded.

## Qualification of archive read/write

A *writable mount* is a filesystem presentation, distinct from the
`FormatCapabilities` archive tier. WORM means an archive can be **created**
once through `IArchiveCreatable`; it does not mean its existing bytes can be
modified in place. Nevertheless, a WORM archive can offer mounted R/W by
rebuilding a **new** complete archive from the modified file tree and
publishing that image only after validation.

A registered multi-entry archive can be mounted read/write if all of the
following hold:

- It has `IArchiveFormatOperations` for listing and reading entries, and
  either `CanModify` with `IArchiveModifiable`, or `CanCreate` with
  `IArchiveCreatable`. A class name or extension alone is insufficient.
- It is a true `FormatCategory.Archive` or `FormatCategory.CompoundTar`, not a media/executable pseudo-archive,
  a registered filesystem, or an inner block-device provider.
- Its source contains only ordinary, unencrypted, non-link entries with safe,
  unique relative names. No password is used.
- ZIP and TAR are now independently qualified for disk-spooled staging when their total uncompressed contents exceed **64 MiB**. Other archives, including 7z, RAR, WIM and compound tar.gz, retain the 64 MiB in-memory cap until the exact large-member writer is qualified. Disk-backed inode lengths and positions may exceed 2 GiB; available disk capacity, the archive format and the chosen editor still impose practical limits.
- The backing source is readable, writable and seekable.
- A **WORM-only** descriptor must first pass a non-destructive no-op rebuild:
  recreate the existing entries in a temporary file, then compare names,
  kinds, bytes, known modification times and semantic container properties
  through `ArchiveSemanticManifest`. Refuse writable qualification if the
  exact existing profile cannot round-trip.
- Any `IArchiveWriteConstraints` rejection prevents publication. Directory
  creation is advertised only when `SupportsDirectories` is set; implied
  parent directories do not require separate stored directory records.

### Write-back strategies

| Capability | Implementation | Publication boundary |
| --- | --- | --- |
| Existing archive editor (`IArchiveModifiable`) | Apply archive-native add/remove operations on a private candidate; ZIP and TAR with large contents stage file data on disk | Verify candidate and publish at `Flush` |
| Explicit native in-place editor (`IArchiveInPlaceMountWriter`, currently ZIP) | For a named FileStream only, persist a full-size SHA-256 undo journal beside the original, edit the live stream, verify all data and known metadata; on failure roll back and on next mount recover any surviving journal | Journal-backed direct `Flush` |
| WORM-only creator (`IArchiveCreatable`) | Construct complete replacement from the mounted file tree through the existing archive writer | Validate candidate, publish at `Flush` |
| Read-only parser only | No writes; retain parsed hierarchy for reading | None |

Both mutable paths reuse `MutableRebuildFilesystemSession` and
`WholeImageRebuildCommitter.Replace`. Open handles retain stable node IDs
across rename/unlink; `Flush` is the durable transaction boundary. A rejected
candidate leaves the original image unchanged. For an ordinary host-backed file,
publication is **not crash-atomic**; rollback can recover ordinary I/O errors,
not every possible process or power failure. Never edit the source in another
application while it is mounted.

Ordinary native archive editors mutate **only a private candidate**. ZIP is the first explicitly qualified exception: if opened from a named, writable file, the mount uses `JournaledArchiveWriteback` to persist a same-directory undo image before any live change. A failed mutation or post-write verification replays the undo. At a later mount, a surviving valid journal is recovered before the source is exposed. A corrupt journal fails closed and is retained for manual investigation. A read-only open with pending journal is refused. Native direct mutation is not inferred from `CanModify` alone.

**Power-failure caveat:** managed `Flush(true)` covers data fsync, but cannot portably fsync the parent directory after journal creation/deletion. The undo record may therefore lack a directory-durability guarantee on some filesystems. Do not claim crash-atomic transactions in this mode; use staged candidate publication where that stronger property is required.

The original 64 MiB cap remains for formats whose writers may internally buffer complete inputs (notably 7z, RAR, WIM and compound tar.gz). The mount-neutral inode layer now supports disk-backed staging with positional `RandomAccess` and stream-oriented reconstruction for ZIP/TAR, including files larger than `Array.MaxLength`. Streaming beyond this bound still requires a format-qualified writer and sufficient temporary disk space.

## Named multi-file sources

Read-only mounts opened from a filesystem path retain source identity for
archive descriptors whose namespace refers to companion files. CloneCD
`.ccd` files can therefore expose adjacent `.img` and `.sub` files;
`CloneCdFormatDescriptor.OpenEntry` streams known companions directly without
copying a whole CD image into every opened file handle. `metadata.ini` has
its actual generated length.

This named-file behavior applies to read-only mounts. Write-back mounts always
use their separate immutable snapshot; they never edit companion files merely
because a primary file is writable.

## Format examples and honest limits

| Input family | Read-only | Read/write |
| --- | --- | --- |
| ZIP | Registered archive listing and entry streams | Disk-spooled, no 64 MiB logical cap; named files may use explicit in-place edits with persistent undo journal |
| TAR | Registered archive listing and entry streams | Disk-spooled, no 64 MiB logical cap; native editor staged on a private output |
| 7z / RAR and other native-edit archives | Registered archive listing and entry streams | Qualified unencrypted profiles within current 64 MiB materialization cap |
| WORM-only multi-entry archives (e.g. WIM with named files, tar.gz) | Registered archive listing and entry streams | Qualified per-image via temporary full rebuild with verified no-op preflight |
| ISO / FAT / NTFS / ext4 / btrfs | Existing managed filesystem driver or derived archive listing when the precise image is supported | **Only** an actual writable native filesystem profile; no implied support from the file extension |
| VMDK / other registered block containers | Registered container → recognized inner filesystem(s) and raw fallback partitions | Only a single native writable inner filesystem via its supported block provider; composite partition root is read-only |
| CloneCD `.ccd/.img/.sub` | Existing pseudo-archive, with companion-file identity retained | No qualified mounted write-back |
| Generic `.img` | Content-detected known filesystem where available; otherwise explicit `Raw`/`Unknown` exposes original bytes | Only if the detected native filesystem and the backing source advertise complete writes |
| `.d90` not registered | Explicit `Raw`/`Unknown` byte-for-byte single-file view | No Commodore D90 filesystem writer from this bridge |
| DoubleSpace CVF | Existing registered archive projection | No native mount-grade writer qualified by this bridge |
| Video, audio, images, executables | Existing structured pseudo-archive when implemented; otherwise one opaque original file | No fabricated writable semantics |

All write capability decisions belong to the actual per-image
`FilesystemDriverProfile`, not the source extension, the host operating
system, or `FormatCapabilities.CanModify` by itself. A creator-only WORM
format may be writable **as a mount** without claiming in-place archive
modification at the descriptor API tier.

## References

- [libfuse low-level operations](https://libfuse.github.io/doxygen/structfuse__lowlevel__ops.html)
- [PKWARE ZIP APPNOTE](https://pkware.cachefly.net/webdocs/casestudies/APPNOTE.TXT)
- [7z container specification](https://www.7-zip.org/7z.html)
- [RAR archive technote](https://www.rarlab.com/technote.htm)
- [VICE D90 disk image specification](https://vice-emu.sourceforge.io/vice_17.html)
- [CloneCD CCD/IMG/SUB layout](https://psx-spx.consoledev.net/dev/ps1/cdr/cdromfileformats/diskimages/)


## Native Linux FUSE validation

The `NativeFuseArchiveMountIntegrationTests` fixture exercises ZIP, 7z,
RAR, WIM and tar.gz through a real libfuse3 mount. It performs file creation,
positional writes, truncate, rename, deletion, flush/fsync, unmount and reopen
via the OS VFS. It carries `OsIntegration` and `NativeFuse` categories.

The Linux CI job installs libfuse3 and attempts to prepare `/dev/fuse` before
invoking this test fixture. GitHub-hosted environments may reject real FUSE
mounts; the job is advisory and missing device/library requirements are marked
as **skipped**. Such skips are not counted as successful kernel validation.
The mount-neutral unit tests remain the mandatory cross-platform gate.
