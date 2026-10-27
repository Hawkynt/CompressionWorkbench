# SVR4 BFS reference volume

`kernel.bfs.gz` is a 64 KiB Boot File System volume that CompressionWorkbench wrote none
of: `mkfs.bfs` formatted it and the Linux `bfs` driver wrote every file. It is gzipped only
to keep the repository small; the tests decompress it in memory.

| | |
| --- | --- |
| Formatted by | `mkfs.bfs -V kvol01 -F kfs001` from util-linux 2.39.3 (Ubuntu 24.04) |
| Written by | Linux `6.8.0-147-generic` `bfs` module inside the libguestfs appliance |
| Recipe | `generate.sh <output-dir>` |
| Created | 2026-10-23 |
| Manifest | `kernel.bfs.manifest`: `sha256sum` of every file as the same driver read it back (`tar-out`) |

What it covers: a two-block file, a file in a non-default mode (`0600`), an empty file
(start block 0, end offset `0xFFFFFFFF`), a hard link (`nlink` 2, two names), and the hole
a deleted file leaves — its inode slot zeroed, its directory slot cleared, its data block
left behind.

`random.bin` comes from `/dev/urandom`, so regenerating changes the image and the manifest
together; the tests only compare the two.
