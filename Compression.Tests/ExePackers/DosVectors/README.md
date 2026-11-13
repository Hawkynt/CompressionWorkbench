# DOS executable packer vectors

Real packer output for the DOS unpackers, with the restoring tools' output kept beside it
where it differs from the original.

## Inputs

Test programs of this repository, assembled with NASM:

| File | Source | Parameters |
|---|---|---|
| `T1.EXE` | `t.asm` via `build.py` | `NREL=4 NTEXT=40 NRAND=512 SEED=12345 NFAR=2` |
| `T2.EXE` | `t.asm` via `build.py` | `NREL=300 NTEXT=200 NRAND=6000 SEED=777 NFAR=40` |

The programs have relocations in two segments, text and pseudo-random data, 0x40 paragraphs
of extra memory and the stack above the image; each prints `OK` only when every relocated
pointer holds its segment and the data checksum matches, so a wrongly restored program says
`BAD`.

## Packed files and oracle output

All tools ran under DOSBox-X 2026.10.01 (Windows build). Every packed file and every restored
file below printed `OK` there. `P<n>` is `T<n>` packed; `U<n>` is UNP 4.11's restoration of
it (`UNP e -k- P<n> U<n>`), committed only where it differs from `T<n>`.

| Files | Packer | Download (sha256) | Oracle result |
|---|---|---|---|
| `lzexe091.P1/P2.EXE` | LZEXE 0.91, `LZEXE P<n>.EXE` | sac.sk `lzexe091.zip` (`0f362c88…669142bf`) | `lzexe.U<n>.EXE` |
| `lzexe100.P1/P2.EXE` | LZEXE 1.00a, same command | sac.sk `lzexe100.zip` (`ac0277ee…7be6fe75`) | `lzexe.U<n>.EXE` (identical to the 0.91 restorations) |

UNP 4.11: `unp411.zip` (sha256 `45681a35…4fc342bf`), from the F1GP file archive.

### Where the unpackers and UNP differ

- **LZEXE** keeps only the linear address of each relocation, so UNP and the unpacker both
  write them paragraph-normalised; UNP also writes its own minimum allocation, while the
  unpacker restores the value LZEXE recorded. Ours is UNP's output with bytes 10-11 equal to
  the original's.
