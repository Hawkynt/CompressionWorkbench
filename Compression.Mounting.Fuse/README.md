# Compression.Mounting.Fuse

Linux FUSE3 transport for mount-grade `IFilesystemSession` implementations.

The backend does **not** parse filesystem images. CompressionWorkbench opens and parses every source layer before this project sees the namespace:

`source bytes -> CompressionWorkbench parsers -> IFilesystemSession -> FUSE3`

## Read/write qualification

- Linux x86-64 ABI, FUSE3 low-level/inode API;
- read-only and read/write mounting when the **image**, **filesystem profile**, and **mount plan** permit it;
- stable inode mapping from `FilesystemNodeId`, lookup/forget accounting and zero-reference reclamation;
- open file and directory handles pin inode state until release, surviving rename and unlink;
- positional file reads and writes, including append and truncation;
- create, mknod (regular files only), unlink, mkdir, rmdir, rename with `RENAME_NOREPLACE`, symlink and hard link, each guarded by the filesystem's advertised capabilities;
- `setattr` size and access/modification timestamps (including `*_NOW`) when `Truncate` or `SetMetadata` is advertised; unsupported POSIX metadata changes fail explicitly;
- directory snapshots with `.` / `..`, stable per-handle offsets and snapshot-lifetime inode pins;
- explicit `access(2)` handling including read-only mounts, write capability checks and directory search;
- `flush`/`fsync` forwarding for writable handles and filesystem publication, with a final flush at unmount;
- single-threaded FUSE session loop until callback concurrency is qualified;
- teardown explicitly wakes the legacy single-threaded receive loop with an uncached lookup before unmounting, because `fuse_session_exit()` alone does not wake `fuse_session_loop()`.

The writable baseline is **capability-driven**, not a promise that every archive or filesystem is writable. A mount requires a writable backing source and a filesystem session advertising `CanMountWritable`, a safe mutation model, `WriteData`, `Truncate`, `CreateFile`, `DeleteFile`, `Rename` and `Flush`. Directory and link operations are optional because some legitimate writable formats have flat namespaces. A read-only mount consistently rejects changes with `EROFS`.

## Validation

The unit tests exercise inode/handle lifetime, directory snapshots, read-only rejection, timestamp mutation, invalid offsets and unsupported operations. The D64 writable adapter test verifies that creates, writes, truncation, rename, unlink and flush survive reopening the image. The OS-integration ROMFS test executes those operations through a real native mount and verifies persistence after unmount. OS integration is skipped when the host runtime is unavailable.

Runtime mounting requires the host-provided `libfuse3.so.3` and `/dev/fuse`. Unprivileged mounting additionally requires `fusermount3`; privileged/root mounting uses libfuse's direct mount path. No FUSE NuGet package is used.

## Limits

- Only the Linux x86-64 native ABI has been qualified.
- Permissions reported to the kernel are synthesized from the mount mode and node kind. Owner/group changes, `chmod`, `ctime` mutation, device-node creation, extended attributes and other POSIX features not exposed by `IFilesystemSession` are **not** silently emulated. They return an unsupported-operation error where requested.
- Filesystem capacity/free-space statistics cannot be authoritative until the mount-neutral contract exposes that information. `statfs` currently returns `ENOSYS`.
- FUSE requests run on a single callback loop. Concurrent file access still needs qualification before multithreaded dispatch is enabled.
- The backing filesystem implementation controls on-disk correctness, link semantics, buffering and transaction durability. The FUSE transport never assumes unsupported operations work.

## Reference and licensing

Native declarations and ABI layouts follow the public libfuse3 headers and low-level API documentation (`fuse_lowlevel.h`, `fuse_common.h`) and the upstream `hello_ll` example for lifecycle and directory enumeration. Timestamp flags and `setattr` behavior follow the [official low-level API](https://libfuse.github.io/doxygen/fuse__lowlevel_8h_source.html). The shutdown wake follows the public legacy-loop contract and [libfuse issue #1572](https://github.com/libfuse/libfuse/issues/1572). No libfuse implementation code is copied.

libfuse is LGPL-2.1-or-later; this backend dynamically links the system-provided shared library and keeps the native dependency behind the mounting backend boundary.
