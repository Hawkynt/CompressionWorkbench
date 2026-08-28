# Filesystem driver architecture

The archive API is useful for whole-image tooling, but it is deliberately **not** the contract for a future mounted filesystem driver. `CanModify` means an existing container can be changed and verified; that change may legally be implemented by a whole-image rebuild. A mounted writable filesystem has stronger requirements.

## Layers

1. **Container / media layer** — VHD/QCOW2/EWF expose logical blocks; G64/NIB/flux formats expose raw tracks until a sector decoder can project them as blocks.
2. **Block device layer** — `IRandomAccessBlockDevice` provides positional logical-block I/O, flush and trim independent of the outer container.
3. **Filesystem namespace layer** — `IFilesystemDriverProvider` probes the exact on-disk profile and opens an `IFilesystemSession` over stable node identities.
4. **OS adapter** — FUSE, WinFsp, Dokany or another adapter translates kernel requests into the session contract. It should contain no filesystem-specific parsing.

The dependency direction is one-way: a FAT/ext/ReFS driver consumes a block device; it does not know how QCOW2/EWF stores that block. Likewise, G64 is not a CBM-DOS filesystem. It is a track device which may later be decoded into sectors and mounted by a CBM-DOS driver.

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

## Current first lower-layer implementation

`CbmNibbleRawTrackDevices` implements the raw-track layer for G64/NIB:

- NIB writes one fixed 8192-byte slot directly and can clear a slot without moving anything else.
- G64 keeps half-track index as stable device identity, stages variable-length track changes, rebuilds the offset table at flush, and verifies every surviving track before commit.
- G64 images using pointer-based variable-speed maps are readable but writable raw-track open is refused until those auxiliary speed-map blocks are modeled.

The next CBM step is a GCR sector projector implementing `IRandomAccessBlockDevice`; the existing D64/CBM filesystem logic can then be refactored to consume that block-device contract rather than owning an outer image format directly.
