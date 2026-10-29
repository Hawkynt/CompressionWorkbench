# JFFS v1 reference images

Nothing in this repository wrote these images. `mkfs.jffs` built both, from the same
tree; they are gzipped only to keep the repository small.

| | |
| --- | --- |
| Built by | `mkfs.jffs` from mtd-utils, as kept in DSLinux (`user/mtd-utils/mkfs.jffs.c`, GPL), compiled with gcc on Ubuntu 24.04 (WSL); the fetch and build commands are in `generate.sh` |
| `jffs1-le` | `mkfs.jffs -q -d src -o out` (host order: little-endian) |
| `jffs1-be` | `mkfs.jffs -q -a big -d src -o out` |
| Created | 2026-10-26 |
| Manifests | the source tree as given to the tool: `F mode size sha256 path`, `D mode path`, `L mode path -> target` |

The tree covers nested and empty directories, an empty file, a 108,894-byte file split
across four 32 KiB data nodes, incompressible data, a symlink and non-default modes.
`random.bin` comes from `/dev/urandom`, so regenerating changes the images and the
manifests together.
