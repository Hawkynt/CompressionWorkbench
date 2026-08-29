# Filesystem driver architecture

The archive API is useful for whole-image tooling, but it is deliberately **not** the contract for a future mounted filesystem driver. `CanModify` means an existing container can be changed and verified; that change may legally be implemented by a whole-image rebuild. A mounted writable filesystem has stronger requirements.

## Layers

1. **Container / media layer** — VHD/QCOW2/EWF expose logical blocks; G64/NIB/flux formats expose raw tracks until a sector decoder can project them as blocks.
2. **Block device layer** — `IRandomAccessBlockDevice` provides positional logical-block I/O, flush and trim independent of the outer container.
3. **Filesystem namespace layer** — `IFilesystemDriverProvider` probes the exact on-disk profile and opens an `IFilesystemSession` over stable node identities.
4. **OS adapter** — FUSE, WinFsp, Dokany or another adapter translates kernel requests into the session contract. It should contain no filesystem-specific parsing.

The dependency direction is one-way: a FAT/ext/ReFS driver consumes a block device; it does not know how QCOW2/EWF stores that block. Likewise, G64 is not a CBM-DOS filesystem. It is a track device which may later be decoded into sectors and mounted by a CBM-DOS driver.

## Repository-wide coverage invariant

Filesystem-driver coverage is no longer a hand-maintained shortlist.

`Compression.Lib` and `Hawkynt.FileFormats.FileSystems` reference `FileSystems/FileSystem.*/*.csproj` through an exhaustive project glob. The registry source generator marks descriptors declared below a `FileSystem.*` namespace as filesystem descriptors and discovers public `IFilesystemDriverAdapter` sidecars without reflection. Registry initialization then requires every marked filesystem descriptor to have one of these paths:

- the descriptor itself implements a native `IFilesystemDriverProvider`;
- a generated native `IFilesystemDriverAdapter` sidecar exists for its format ID; or
- the descriptor implements `IArchiveFormatOperations` and can be projected conservatively through the read-only archive-derived filesystem session.

`Compression.Tests/Operations/FilesystemDriverCoverageTests.cs` enforces the same rule in CI. Adding a new `FileSystem.*` descriptor that has no derivable driver path is therefore a build/test regression rather than a documentation omission.

The derived archive projection is intentionally a **compatibility floor**, not a native-readiness claim. It supplies a bounded, positional, read-only namespace for formats whose parser can list/open entries; its default `OpenEntry` path is temporary-file-backed so large files do not require one `byte[]`. It cannot by itself prove native inode identity, allocation maps, sparse/shared extent semantics, crash recovery or mounted writes.

`FormatRegistry.GetFilesystemDriverCoverage()` exposes whether a format currently resolves through a native descriptor provider, a generated native sidecar, or the conservative archive projection. `IFilesystemDriverReadinessProvider` separately reports which implementation layers are genuinely present for read-only and read/write driver targets.

## Requirements for writable mounting

A format may advertise archive-level `CanModify` while its `FilesystemDriverProfile.CanMountWritable` remains false. Writable mounting should only be enabled when all of the following are true for the **current image profile**:

- stable object identity survives rename and open-handle lifetime;
- directories support lookup and enumeration without path-as-identity shortcuts;
- file reads and writes are positional/random-access and bounded to the file;
- create, unlink, mkdir, rmdir, rename and truncate update all relevant indexes and allocation metadata;
- free-space allocation and release are known, including sparse/shared/extents where applicable;
- dirty metadata has a defined flush/durability boundary;
- crash consistency is understood (journal, CoW transaction, log commit, or another native scheme);
- checksums, mirrors, generations, sequence numbers and backup metadata are updated together;
- unknown feature bits or metadata layouts force a read-only profile instead of being guessed;
- concurrent open handles cannot race through one shared `Stream.Position` cursor.

`FilesystemMutationModel.WholeImageRebuild` is therefore useful for offline tools but should normally report `CanMountWritable = false`.

## Transactions

`IFilesystemSession.BeginTransaction()` is an explicit durability boundary. Implementations with native transactions should map it to their real mechanism:

- ReFS: CoW metadata pages + allocation updates + checkpoint/SUPB publication;
- APFS/Btrfs/ZFS: native CoW transaction groups / roots;
- NTFS: logfile-aware metadata update sequence;
- FAT-like filesystems: a staged write set ordered so allocation, directory entry and size changes cannot expose uninitialised clusters.

A driver without a safe native commit model can still be mounted read-only and can continue using the archive rebuild path for offline edits.

## Identity and handles

`FilesystemNodeId` is intentionally path-independent. Providers should map a native inode/object/file reference plus generation/sequence information where available. FAT-like formats may synthesize identity from stable directory-slot/first-cluster information, but must invalidate stale generations when a slot is reused.

`IFilesystemFileHandle` uses positional `Read(offset, ...)` and `Write(offset, ...)`, not a mutable stream cursor. This matches kernel I/O request semantics and permits independent concurrent handles.

`SpoolingReadOnlyFileHandle` is a transitional handle for readers that can stream the correct logical file but do not yet expose a seekable native extent map. Small files stay in memory; larger files use a delete-on-close temporary file. Filesystems with a verified extent mapping should prefer a direct positional handle instead.

## Native driver tier currently implemented

The following formats now have native descriptor providers or generated native sidecars rather than relying only on archive projection:

- **D64 / CBM nibble media** — native Commodore namespace plus sector/raw-track layers; G64/NIB retain raw-track identity and the GCR sector projector feeds the filesystem layer.
- **ReFS** — native read session over decoded metadata/object identities; mounted mutation remains separately gated from the offline-quiescent image editor.
- **FAT12/16/32** — native directory/cluster-chain read session and allocation knowledge; mounted mutation remains gated on a proven ordered durability protocol.
- **ext2/ext3/ext4** — native inode identity and positional block/extent reads; mounted mutation remains gated on complete allocation, metadata and journal transaction semantics.
- **NTFS** — native MFT-record identity and positional data reads; full file-reference sequence identity, index mutation, `$LogFile` publication/replay and complete metadata/security semantics remain write blockers.
- **XFS** — native inode+generation identity and streaming extent reads; writable mounting remains gated on allocation-group btrees, log transactions, delayed allocation/refcount/reflink and crash-recovery semantics.
- **APFS** — native object IDs and direct positional reads for the proven unencrypted single-extent profile; broader extent/compression/encryption profiles fail closed and CoW checkpoint/spaceman/OMAP publication remains a write blocker.
- **Btrfs** — native inode IDs and a global logical segment map across all FS-tree leaves. Inline/regular extents are read directly, holes/prealloc ranges return zeroes, and compressed/encrypted/multi-stripe profiles fail closed. Writable mounting remains gated on CoW tree paths, delayed refs, extent/checksum/free-space trees, transaction generations and log-tree replay.
- **ZFS** — native dataset dnode object IDs and checksum-verified streaming reads for the supported v28 single-vdev profile. Unsupported compression/checksum/hole/gang/multi-vdev profiles fail closed. Writable mounting remains gated on metaslab/spacemap allocation, TXG/uberblock publication, ZIL replay and complete DSL/ZAP/dnode semantics.

A native read-only sidecar is not a promise that the existing archive-level `Add`/`Remove` path is safe to expose to a mounted kernel client. `CanMountWritable` is only enabled when the corresponding readiness report can satisfy the full read/write layer set.

## Raw-track lower layer

`CbmNibbleRawTrackDevices` implements the raw-track layer for G64/NIB:

- NIB writes one fixed 8192-byte slot directly and can clear a slot without moving anything else.
- G64 keeps half-track index as stable device identity, stages variable-length track changes, rebuilds the offset table at flush, and verifies every surviving track before commit.
- G64 images using pointer-based variable-speed maps are readable but writable raw-track open is refused until those auxiliary speed-map blocks are modeled.

The GCR sector projector implements the bridge toward `IRandomAccessBlockDevice`, allowing Commodore filesystem logic to consume sector semantics without treating a variable-length raw-track container as if it were itself the filesystem.
