# SQX reference archives

Taken unchanged from the `packed_corpus` directory of Squeez-c
(https://github.com/seb3773/Squeez-c, MIT, revision `9eb5b385ce775aa50a47fd4d35fdc26dbfe7d8e9`),
which describes them as reference historical SQX archives: one source file per archive, in SQX
format 1.1 or 2.0, at the level in the name (`none` stored, `fast` to `maximum` methods 1-4).
`SqxReferenceTests` extracts each and compares the file with the original's size and SHA-256.

| File | Format | Method | Member |
|---|---|---|---|
| `hello_v1.1_fast.sqx`, `hello_v1.1_none.sqx` | 1.1 | 0 | `hello.txt` |
| `code_v1.1_normal.sqx` | 1.1 | 2 | `code.c` |
| `eng_v1.1_high.sqx` | 1.1 | 3 | `eng.txt` |
| `image_v1.1_maximum.sqx` | 1.1 | 4 | `image.raw` |
| `pattern_v1.1_fast.sqx` | 1.1 | 1 | `pattern.bin` |
| `zeros_v1.1_maximum.sqx` | 1.1 | 4 | `zeros.bin` |
| `image_v2.0_maximum.sqx` | 2.0 | 4 | `image.raw` |
| `image24_v2.0_maximum.sqx` | 2.0 | 4 | `image24.raw` |
| `pattern_v2.0_maximum.sqx` | 2.0 | 4 | `pattern.bin` |
| `mixed_v2.0_normal.sqx` | 2.0 | 2 | `mixed.bin` |
| `biased_v2.0_normal.sqx` | 2.0 | 2 | `biased.bin` |
| `empty_v2.0_none.sqx` | 2.0 | 0 | `empty.dat` |
| `hello_v2.0_normal.sqx`, `code_v2.0_ultra.sqx`, `eng_v2.0_ultra.sqx` | 2.0 | 6 (PPMd) | `hello.txt`, `code.c`, `eng.txt` |
| `ppmd_v2.0_order2.sqx`, `ppmd_v2.0_order16.sqx` | 2.0 | 6 (PPMd) | `eng.txt`, written by Squeez-c's own `sqx create --fmt=20 --method=6 --ppm-order=2` (default 32 MiB) and `--ppm-order=16 --ppm-mem=4` |
| `biased_v2.0_ultra.sqx`, `image_v2.0_ultra.sqx`, `image24_v2.0_ultra.sqx`, `mixed_v2.0_ultra.sqx` | 2.0 | 7 (ultra) | `biased.bin`, `image.raw`, `image24.raw`, `mixed.bin` |
| `pattern_v2.0_ultra.sqx`, `records_v2.0_ultra.sqx`, `zeros_v2.0_ultra.sqx`, `empty_v2.0_ultra.sqx` | 2.0 | 7 (ultra) | `pattern.bin`, `records.bin`, `zeros.bin`, `empty.dat` |
| `words300_v1.1_sqc52_normal.sqx`, `words300_v2.0_sqc52_best.sqx` | 1.1, 2.0 | 2, 4 | `words300.txt` (300000 bytes of `word i%977 i*31%1013` lines), written by Squeez CommandLine 5.2 for Linux (`sqc a -ep -fmt SQX1 -m2`, `-fmt SQX2 -m4`; from `sqc_linux.tgz` of https://github.com/wesinator/sqx-compression): matches reach back further than 256 KiB, where the distance codes depend on the dictionary size |

| `Noise_v1.1_none.sqx`, `test_v1.1_normal.sqx` | 1.1 | 5 (audio) | `Noise.wav`, `test.wav` (16-bit mono) |
| `Noise_v2.0_maximum.sqx` | 2.0 | 5 (audio) | `Noise.wav` (16-bit mono) |
| `e_sin8_v2.0_audio.sqx`, `e_st8_v2.0_audio.sqx`, `x_alt8_v2.0_audio.sqx`, `sine_st8_v2.0_audio.sqx` | 2.0 | 5 (audio) | 8-bit mono and stereo, written by Squeez-c's own `sqx create --fmt=20 --method=5` |
| `x_altst16_v2.0_audio.sqx`, `x_5003st16_v2.0_audio.sqx`, `x_ramp16_v2.0_audio.sqx` | 2.0 | 5 (audio) | 16-bit stereo and mono, written by Squeez-c's own encoder |

The three `Noise_*`/`test_*` archives are from Squeez-c's `corpus` (archives the original SQX
archiver wrote), with their WAV files; the other audio sources are in the same `corpus`, except
`sine_st8.wav`, a generated 8-bit stereo sine pair (24044 bytes, SHA-256 `8ad1101a…2fa1`).

The `*_ultra.sqx` archives come from the same `packed_corpus` at revision
`03211cd1e86473da08bc675198f6902e15a28ca3`; `records.bin` (240000 bytes) is in its `corpus`.
