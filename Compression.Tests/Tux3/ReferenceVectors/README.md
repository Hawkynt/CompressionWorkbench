# Tux3 reference volumes

16 MiB Tux3 volumes (4 KiB blocks, revision 2015-06-01) that the linux-tux3 user tools wrote,
gzipped (`gzip -9 -n`) only to keep the repository small. Nothing in this repository wrote them.

| | |
| --- | --- |
| Tools | `tux3` and `tux3fuse` from linux-tux3 (https://github.com/OGAWAHirofumi/linux-tux3, branch `hirofumi-user`, `fs/tux3/user`), built on Ubuntu 24.04 (WSL) with `make UCFLAGS=-Wno-error` |
| Recipe | `tux3-gen.sh <output-dir>` |
| Created | 2026-11-24 |
| Oracle | `<volume>.manifest`: what `tux3fuse` lists and reads back from the image after a fresh mount (`D path`, `L path -> target`, `F path size mode links sha256`) |

| File | How it was made |
| --- | --- |
| `tree.img.gz` | `tux3 mkfs`, then `tux3fuse` mounted it and the tree was written through it: nested and empty directories, small files (0 to 2000 bytes), a directory of 150 entries |
| `cli.img.gz` | `tux3 mkfs`, then `tux3 write` (one run each) stored `big.bin`, `text.txt`, `hello.txt`, `empty`, `f4096` and `f4097` in the root |
| `links.img.gz` | `tux3 mkfs`, `tux3 write hello.txt`, `tux3 link hello.txt hard-hello`, `tux3 symlink hello.txt sym-hello` |
| `*-unified.img.gz` | each of the above after `tux3 unify` folded its log into the trees |

The tools of this branch are not dependable writers, and the volumes keep to what they store
reliably: `tux3fuse` aborts on writes of 4 KiB and more, so the tree holds small files; `tux3 write`
leaves files of a block and more at size 0 (`tux3fuse` reads them back that way, and so does the
reader); a name given to `tux3 write` with a leading `/` is stored with the slash, which `tux3fuse`
then cannot list. `tux3 fsck` accepts every volume. Random contents come from `/dev/urandom`, so
regenerating changes the images and the manifests together.
