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
- single-threaded FUSE session loop until callback concurrency is qualified.

Runtime mounting requires the host-provided `libfuse3.so.3`, `/dev/fuse`, and `fusermount3`. No FUSE NuGet package is used.

## Reference and licensing

The native declarations and ABI layouts are derived from the public libfuse3 headers and low-level API documentation (`fuse_lowlevel.h`, `fuse_common.h`) and the upstream `hello_ll` example for lifecycle and directory enumeration behavior. The repository does not copy libfuse implementation code.

libfuse is LGPL-2.1-or-later; this backend dynamically links the system-provided shared library and keeps the native dependency behind the mounting backend boundary.