# Compression.Mounting.Fuse

Linux FUSE3 transport for mount-grade `IFilesystemSession` implementations.

The backend does **not** parse filesystem images. CompressionWorkbench opens and parses every source layer before this project sees the namespace:

`source bytes -> CompressionWorkbench parsers -> IFilesystemSession -> FUSE3`

## Current qualification

- Linux x86-64 ABI;
- FUSE3 low-level/inode API;
- read-only mounting only;
- stable inode mapping from `FilesystemNodeId`;
- lookup/forget accounting with zero-reference inode reclamation;
- open file and directory handles pin inode state until release;
- stable open file handles using `IFilesystemFileHandle`;
- positional reads;
- directory snapshots with `.` / `..` and stable per-handle offsets;
- readdir-only child inode mappings live for the snapshot lifetime without falsely incrementing FUSE lookup counts;
- explicit `access(2)` handling for read, execute, and read-only write checks;
- read-only `EROFS` responses for mutating callbacks;
- flush/fsync forwarding when the filesystem profile advertises `Flush`;
- single-threaded FUSE session loop until callback concurrency is qualified;
- teardown explicitly wakes the legacy single-threaded receive loop with an uncached lookup before unmounting, because `fuse_session_exit()` alone does not wake `fuse_session_loop()`;
- OS-integration smoke coverage mounts a synthetic namespace, enumerates and reads it through the kernel, keeps a file open during teardown, and verifies clean unmount whenever the host runtime is available.

Runtime mounting requires the host-provided `libfuse3.so.3` and `/dev/fuse`. Unprivileged mounting additionally requires `fusermount3`; privileged/root mounting does not, because libfuse can use the direct mount path. No FUSE NuGet package is used.

## Reference and licensing

The native declarations and ABI layouts are derived from the public libfuse3 headers and low-level API documentation (`fuse_lowlevel.h`, `fuse_common.h`) and the upstream `hello_ll` example for lifecycle and directory enumeration behavior. The shutdown wake follows the public legacy-loop contract and the upstream discussion in [libfuse issue #1572](https://github.com/libfuse/libfuse/issues/1572); no libfuse implementation code is copied.

libfuse is LGPL-2.1-or-later; this backend dynamically links the system-provided shared library and keeps the native dependency behind the mounting backend boundary.
