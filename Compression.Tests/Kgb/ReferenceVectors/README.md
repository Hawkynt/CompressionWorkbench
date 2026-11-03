# KGB Archiver reference archives

Every `.kgb` file here was written by Debian's **kgb 1.0b4+ds-14** (KGB Archiver 1.0 by Tomasz
Pawlak, POSIX port by Slawek; GPL-2.0-or-later), installed with `apt install kgb` on Ubuntu 24.04
under WSL, on 2026-11-03.

`mkfixtures.py` (documentation, not a build input) generated the inputs deterministically, ran
`kgb -<M> kgb-m<M>.kgb <files>` in the input directory with the names sorted, extracted every
archive again with `kgb kgb-m<M>.kgb` and recorded what came out. Only the archives and
`manifest.tsv` are checked in: the manifest gives, per archive, every name with its size and
SHA-256.

| File | Memory level | What it exercises |
|------|--------------|-------------------|
| `kgb-m0.kgb` | 0 | char model only, one mixer, no SSE |
| `kgb-m1.kgb` | 1 | adds SSE and the order-5 map |
| `kgb-m2.kgb` | 2 | adds the match model |
| `kgb-m3.kgb` | 3 (kgb's default) | adds the x86 call model and order-6/7 maps |
| `kgb-m4.kgb` | 4 | adds the record, sparse, analog and word models |
| `kgb-m5.kgb` | 5 | adds the second mixer, run tables and order-8/9 maps |
| `kgb-m6.kgb` | 6 | the reduced size exponents of levels 6 and above |

The inputs: `text.txt` (CR LF prose), `random.bin` (incompressible), `table.bin` (8-byte records),
`sub/code.exe` (x86 `E8` calls among other opcodes), `wave.raw` (16-bit sawtooth samples) and
the empty `empty.dat`.
