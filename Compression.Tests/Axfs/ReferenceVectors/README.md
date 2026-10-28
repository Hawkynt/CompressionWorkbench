# AXFS reference images

Images built by `mkfs.axfs` and by nothing in this repository. They are gzipped only to
keep the repository small; the tests decompress them in memory.

| | |
| --- | --- |
| Built by | `mkfs.axfs` from <https://github.com/jaredeh/axfs> `mkfs.axfs-legacy/` (AXFS 2.2.0), compiled with gcc on Ubuntu 24.04 (WSL) against zlib 1.3 |
| Recipe | `generate.sh <output-dir>` (expects the tool at `~/axfs/mkfs.axfs`) |
| Created | 2026-10-23 |
| `axfs-compressed` | `mkfs.axfs -s src out`: zlib cblocks, plus byte-aligned nodes for pages that do not compress |
| `axfs-xip` | `mkfs.axfs -s -a src out`: the same tree, with the owner-executable ELF file's pages in the XIP region |
| Manifests | the source tree as given to the tool: `F mode size sha256 path`, `D mode path`, `L mode path -> target` |

The tree covers nested and empty directories, an empty file, a file of exactly one page,
files with partial last pages, incompressible data, a duplicate file, a symlink, and
non-default modes. Random contents come from `/dev/urandom`, so regenerating changes the
images and the manifests together.
