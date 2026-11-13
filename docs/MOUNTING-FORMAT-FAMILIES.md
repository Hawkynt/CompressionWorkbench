# Mounting format families

`FilesystemMountLauncher` resolves a source into `IFilesystemSession` **before**
FUSE3 or Dokan receives it. It never delegates source decoding to the host
filesystem or to an external archive/mount executable.

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
   read-only hierarchy. ZIP, 7z, and RAR additionally qualify for staged
   read/write mounting **only** when their actual file profile passes the
   checks below.
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

ZIP, 7z and RAR can be mounted read/write if all of these are true:

- The descriptor exposes the existing `IArchiveModifiable` edit interface.
- `List` succeeds and the archive contains only ordinary files/directories
  with unique and safe relative names. Symlinks and encrypted entries are
  excluded from this write-back profile.
- No password is used; archive mutations with encrypted/password-bound state
  remain read-only.
- Every entry's advertised logical size and the total uncompressed content
  fit the **64 MiB** materialization limit.
- The source is readable, writable and seekable.

Mounted writes use the existing `MutableRebuildFilesystemSession`. Each
`Flush` computes the current linked namespace, starts from an immutable
snapshot of the original archive, applies only required removals/additions with
the archive's own native editor, and validates the exact decoded bytes of every
resulting regular file before publication by
`WholeImageRebuildCommitter.Replace`. Repeated flushes do not replay deletes
against an already changed image. A failed validation does not publish a
candidate. Publication to an ordinary host file is **not crash-atomic**:
a process/machine crash during host-file replacement remains a separate risk.

The 64 MiB bound is intentional: this bridge uses the existing in-memory
mutable session. It is not a promise of unrestricted writable large ZIP/7z/RAR
mounts. Future large-file mounts need a disk-spooled extent-based overlay with
the same transactional validation guarantees.

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
| ZIP / 7z / RAR | Registered archive listing and entry streams | Qualified, bounded unencrypted profiles only |
| ISO / FAT / NTFS / ext4 / btrfs | Existing managed filesystem driver or derived archive listing when the precise image is supported | **Only** an actual writable native filesystem profile; no implied support from the file extension |
| VMDK / other registered block containers | Registered container → recognized inner filesystem(s) and raw fallback partitions | Only a single native writable inner filesystem via its supported block provider; composite partition root is read-only |
| CloneCD `.ccd/.img/.sub` | Existing pseudo-archive, with companion-file identity retained | No qualified mounted write-back |
| Generic `.img` | Content-detected known filesystem where available; otherwise explicit `Raw`/`Unknown` exposes original bytes | Only if the detected native filesystem and the backing source advertise complete writes |
| `.d90` not registered | Explicit `Raw`/`Unknown` byte-for-byte single-file view | No Commodore D90 filesystem writer from this bridge |
| DoubleSpace CVF | Existing registered archive projection | No native mount-grade writer qualified by this bridge |
| Video, audio, images, executables | Existing structured pseudo-archive when implemented; otherwise one opaque original file | No fabricated writable semantics |

All write capability decisions belong to the actual per-image
`FilesystemDriverProfile`, not the source extension, the host operating
system, or `FormatCapabilities.CanModify` by itself.

## References

- [libfuse low-level operations](https://libfuse.github.io/doxygen/structfuse__lowlevel__ops.html)
- [PKWARE ZIP APPNOTE](https://pkware.cachefly.net/webdocs/casestudies/APPNOTE.TXT)
- [7z container specification](https://www.7-zip.org/7z.html)
- [RAR archive technote](https://www.rarlab.com/technote.htm)
- [VICE D90 disk image specification](https://vice-emu.sourceforge.io/vice_17.html)
- [CloneCD CCD/IMG/SUB layout](https://psx-spx.consoledev.net/dev/ps1/cdr/cdromfileformats/diskimages/)
