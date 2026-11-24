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

## Venti reference set

Made by `venti-gen.sh <output-dir>` with the same plan9port build, 2026-11-24. Nothing in this
repository wrote any of it.

| File | What it is |
| --- | --- |
| `venti.arenas.gz` | a 24 MiB arena partition (`fmtarenas -a 4m`, six arenas) that plan9port's `venti` server filled: first `vac` archived the payload, then a `fossil` server archived a snapshot of the same payload (`fsys main snap -a`, `archive vac:…` on its console) |
| `vac.score` | the score `vac` printed for its archive |
| `vac.manifest` | sha256 of every file `unvac` extracted from that score |
| `restored.img.gz` | an 8 MiB Fossil volume made by `flfmt -b 4096 -v <score>` from the Fossil archive's score: its root and every block of its tree live on Venti |
| `restored.manifest` | what the `fossil` server, connected to that `venti`, listed and read over 9P from the restored volume (`D path mode`, `F path size sha256 mode`) |

The payload holds nested and empty directories, an empty file, a 300,000-byte text and a
70,000-byte random file (both larger than one 8 KiB block, so pointer blocks are used), and
modes 775/755/664/644/600. The random files come from `/dev/urandom`, so regenerating changes
the arenas and the manifests together.
