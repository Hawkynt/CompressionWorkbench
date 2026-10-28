# Fossil reference volume

`fossil.img.gz` is a 16 MiB Fossil volume (4 KiB blocks) that nothing in this repository
wrote. It is gzipped only to keep the repository small.

| | |
| --- | --- |
| Formatted by | `flfmt -b 4096` from plan9port, commit `1f1396913fb5372408f442ed26933f5ae4b399cd`, built on Ubuntu 24.04 (WSL) |
| Written by | that plan9port's `fossil` server. Its console created the directories and files; plan9port's `9p` client wrote the data; `fsys main snap` took a local snapshot; `hello.txt` was rewritten after it; `sync`/`halt` closed the volume |
| Recipe | `generate.sh <output-dir>` |
| Created | 2026-10-23 |
| Manifest | sha256 of every active file as the same server read it back over 9P |

The volume covers nested and empty directories, an empty file, a 900,000-byte file
(a two-level pointer tree at 4 KiB blocks), modes 775/755/664/644/600, and a snapshot
whose `hello.txt` keeps the bytes from before the rewrite. `leaf.dat` comes from
`/dev/urandom`, so regenerating changes the image and the manifest together.
