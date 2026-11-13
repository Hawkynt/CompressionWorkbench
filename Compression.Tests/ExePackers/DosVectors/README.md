# DOS executable packer vectors

Real packer output for the DOS unpackers, with the restoring tools' output kept beside it
where it differs from the original.

## Inputs

Test programs of this repository, assembled with NASM:

| File | Source | Parameters |
|---|---|---|
| `T1.EXE` | `t.asm` via `build.py` | `NREL=4 NTEXT=40 NRAND=512 SEED=12345 NFAR=2` |
| `T2.EXE` | `t.asm` via `build.py` | `NREL=300 NTEXT=200 NRAND=6000 SEED=777 NFAR=40` |
| `T5.COM` | `tc.asm` via `buildc.py` | `NTEXT=100 NRAND=3000 SEED=555` |

The EXE programs have relocations in two segments, text and pseudo-random data, 0x40
paragraphs of extra memory and the stack above the image; each prints `OK` only when every
relocated pointer holds its segment and the data checksum matches, so a wrongly restored
program says `BAD`. The COM program checks its data the same way.

## Packed files and oracle output

All tools ran under DOSBox-X 2026.10.01 (Windows build). Every packed file and every restored
file below printed `OK` there. `P<n>` is `T<n>` packed; `U<n>` is UNP 4.11's restoration of
it (`UNP e -k- P<n> U<n>`), committed only where it differs from `T<n>`.

| Files | Packer | Download (sha256) | Oracle result |
|---|---|---|---|
| `lzexe091.P1/P2.EXE` | LZEXE 0.91, `LZEXE P<n>.EXE` | sac.sk `lzexe091.zip` (`0f362c88…669142bf`) | `lzexe.U<n>.EXE` |
| `lzexe100.P1/P2.EXE` | LZEXE 1.00a, same command | sac.sk `lzexe100.zip` (`ac0277ee…7be6fe75`) | `lzexe.U<n>.EXE` (identical to the 0.91 restorations) |
| `pklite115.P1/P2.EXE`, `.P5.COM` | PKLITE 1.15, `PKLITE T<n> P<n>` | simtel20 `PKLTE115.ZIP` | UNP and `PKLITE -x` both give `T<n>` byte for byte |
| `pklite201.P1/P2.EXE`, `.P5.COM` | PKLITE 2.01 shareware, same command | sac.sk `pklts201.exe` (`46095353…f7687436`) | UNP and `PKLITE -x` both give `T<n>` byte for byte |
| `exepack406.P1/P2.EXE` | Microsoft EXEPACK 4.06 (MASM 5.1), `EXEPACK T<n>.EXE P<n>.EXE` | archive.org `microsoft-macro-assembler-5.1-5.25.-7z` | `exepack406.U<n>.EXE` |
| `diet145f.P1/P2.EXE`, `.P5.COM` | DIET 1.45f, `DIET -B P<n>` on a copy | sac.sk `diet145f.zip` (`2af6f269…c9ff715c`) | UNP gives `T<n>` byte for byte |

UNP 4.11: `unp411.zip` (sha256 `45681a35…4fc342bf`), from the F1GP file archive.

### Where the unpackers and UNP differ

- **LZEXE** keeps only the linear address of each relocation, so UNP and the unpacker both
  write them paragraph-normalised; UNP also writes its own minimum allocation, while the
  unpacker restores the value LZEXE recorded. Ours is UNP's output with bytes 10-11 equal to
  the original's.
- **EXEPACK** rounds the image up to whole paragraphs and groups relocations by 64 KiB
  segment; UNP and the unpacker agree on both. The unpacker sizes the minimum allocation to
  keep the packed program's total memory (0x40 and 0x48 paragraphs here), UNP otherwise.
- DIET 1.45f did not run the 176 KiB third test program under DOSBox, and UNP 4.11 cut a
  PKLITE 2.01 restoration of it short at 128 KiB; `PKLITE -x` restored it in full. Neither is
  committed.
