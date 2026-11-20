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
| `refused.hello_v2.0_normal.sqx` (`hello_v2.0_normal.sqx`) | 2.0 | 6 (PPMd) | `hello.txt`, which this reader refuses |
| `words300_v1.1_sqc52_normal.sqx`, `words300_v2.0_sqc52_best.sqx` | 1.1, 2.0 | 2, 4 | `words300.txt` (300000 bytes of `word i%977 i*31%1013` lines), written by Squeez CommandLine 5.2 for Linux (`sqc a -ep -fmt SQX1 -m2`, `-fmt SQX2 -m4`; from `sqc_linux.tgz` of https://github.com/wesinator/sqx-compression): matches reach back further than 256 KiB, where the distance codes depend on the dictionary size |
