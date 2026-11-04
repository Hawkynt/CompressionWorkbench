# BSC reference vectors

Files written by libbsc's own `bsc` tool. They are the test data for `BscLibbscReferenceTests`.

| | |
|---|---|
| Written by | `bsc e <input> <output> [options]` from libbsc 3.3.12 (tag `v3.3.12`, `5e5c2ef0fb1298626936b091f6e4ae539e5b0071`), built single-threaded (no OpenMP) for x86-64 with MinGW-w64 GCC: `g++ -O2 bsc.cpp libbsc/*/*.cpp libbsc/bwt/libsais/libsais.c` |
| Cross-checked | libbsc 3.4.0 (tag `v3.4.0`, `4d823bc57ca1ae6d39a74b244646ee545ff0dcea`), built the same way, writes byte-identical files for every case |
| Inputs | deterministic generators (random words, repeated lines, noise, 32-bit records), mirrored in the test class and in `make-fixtures.py`; only their size and SHA-256 are kept |
| Checked | `bsc d` returned each input before the file was saved |

`manifest.tsv` lists file, decoded size, SHA-256 of the decoded bytes, the bsc options and the file size.
Regenerate with `python make-fixtures.py <bsc> [<another bsc build>]`.

| File | What bsc chose |
|---|---|
| `text-20k.bsc`, `text-20k-nolzp.bsc` | BWT, static QLFC (LZP does not pay, so `-p` changes nothing) |
| `text-20k-e0.bsc`, `text-20k-e2.bsc` | fast and adaptive QLFC |
| `text-20k-cp.bsc` | preceding contexts (block reversed before sorting) |
| `text-100k.bsc` | 12 auxiliary BWT indexes (blocks of 64 KiB and more carry them) |
| `text-300k.bsc` | QLFC in two sub-blocks (BWT output of 256 KiB and more is split) |
| `repeats-50k.bsc`, `repeats-300k.bsc` | LZP (hash 15, minimum match 72); the 300 KB input is LZP-coded in two sub-blocks |
| `records-40k-r.bsc` | 4-byte record reordering (`-r`) |
| `text-60k-b25000.bsc` | three 25 000-byte blocks |
| `text-20k-m3.bsc` .. `text-20k-m6.bsc`, `repeats-50k-m6.bsc` | Sort Transform of order 3 to 6 (orders 7 and 8 need a CUDA build of bsc, so no file covers them) |
| `noise-3000.bsc`, `noise-3000-cp.bsc` | stored: incompressible, so bsc keeps the original bytes and marks the block following |
| `tiny-hello.bsc`, `tiny-hello-cp.bsc`, `one-byte.bsc` | stored: blocks of 28 bytes or less; a preceding-context one is stored reversed |

A bsc built with OpenMP codes the same LZP and QLFC sub-blocks in parallel, but bounds each
sub-block's output differently, so near the incompressible limit it may choose to store where the
single-threaded build codes. Only single-threaded builds were compared byte for byte.
