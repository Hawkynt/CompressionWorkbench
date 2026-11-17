# Append-only sidecar archives (WORM mount mode)

A WORM descriptor remains WORM: `FormatCapabilities.CanCreate` and
`IArchiveCreatable` **do not** imply `CanModify`, nor do they make the
native archive format incrementally writable.

For eligible local WORM archives, `ArchiveOverlayFilesystemSession` provides
a separately qualified `FilesystemMutationModel.LogStructured` writable view.
The on-disk archive stays byte-for-byte unchanged; another archive reader still
sees the original version. The sidecar is **not** a portable archive extension
and must travel with its original archive if the writable view is to be retained.

## Selection and ownership

`MountNamespaceResolver.Probe(..., preferArchiveOverlay: true)` enables the
profile for ordinary archive/compound-TAR descriptors supporting
`IArchiveFormatOperations` + `IArchiveCreatable` but *not* native archive
modification, native filesystem drivers, or block-device providers. Unencrypted,
uniquely named entries with safe relative paths qualify. Parsers requiring a
password, unsafe names, symlinks and encrypted entries are intentionally
excluded. Existing native archive editors, real filesystems and the stream-based
staged rebuild path are unchanged.

`FilesystemMountLauncher` chooses the sidecar for those qualified path-backed
WORM write mounts. For an explicit non-OS-backed session use
`MountNamespaceResolver.OpenOverlayArchivePath(formatId, path)`.

The mount owns three local files:

- `name.ext`: immutable native archive, opened for reading.
- `name.ext.cwb-overlay`: an exclusively locked append-only journal.
- `name.ext.cwbmount.lock`: the existing mount-level coordination lock.

A changing archive identity (path, format, size, modification time and sampled
beginning/end bytes) refuses sidecar replay. This identity is for **accidental
change detection**, not cryptographic protection against a same-size/same-time
change in the middle of a huge archive. Do not modify the source concurrently.

## Mutation and recovery semantics

The sidecar begins with a versioned magic and source identity. Each record has
a bounded length, SHA-256 digest and an operation body. Supported operations:

- create file/directory;
- unlink file/remove empty directory (open handles keep their node identity);
- rename/move with optional replacement;
- positional write, split into bounded 1 MiB records;
- truncate/extend, preserving zero-filled holes rather than resurrecting
  bytes after truncation.

Unmodified data are still read from the archive. Modified bytes are mapped via
non-overlapping overlay ranges; reads merge those ranges with source data and
zero-filled holes. `Flush` calls `FileStream.Flush(true)` on the sidecar. An
interrupted final record is discarded during replay; a *complete but corrupted*
record fails closed without deleting the journal. The operation log is **not**
a multi-operation transaction, and portable parent-directory fsync is not
claimed. Process/crash durability beyond completed, flushed records depends on
the host filesystem.

This design does not modify archive entry names to signal deletion. Tombstones
exist in the sidecar namespace only, with no ambiguity for external readers.

## Commit and compaction boundaries

**Neither `Flush` nor `Dispose` writes into the native archive.** Native
materialization and sidecar reclamation are separate operations. Any future
"publish overlay" operation must verify that the archive writer preserves all
represented semantics, build a candidate, validate its complete manifest, and
atomically publish where the platform allows. Only a format-qualified native
incremental writer may apply deltas directly; filename/comment tombstones are
not assumed to be understood by other readers.

A future overlay-compaction verb can coalesce redundant log records, while the
existing `IArchiveRepackable`/`ICompressionOptimizable` verbs reclaim or
recompress dead **native** archive storage where the format supports it. They
must not silently discard or consume an uncommitted sidecar.

## Verification

`ArchiveOverlayFilesystemSessionTests` covers WORM-profile selection,
persistence, zero-filled sparse ranges, truncate/extend, stable handles over
rename/unlink, read-only native visibility, single-writer exclusion,
interrupted-record recovery, corrupt-record failure and source-identity
mismatch.
