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

| `Noise_v1.1_none.sqx`, `test_v1.1_normal.sqx` | 1.1 | 5 (audio) | `Noise.wav`, `test.wav` (16-bit mono) |
| `Noise_v2.0_maximum.sqx` | 2.0 | 5 (audio) | `Noise.wav` (16-bit mono) |
| `e_sin8_v2.0_audio.sqx`, `e_st8_v2.0_audio.sqx`, `x_alt8_v2.0_audio.sqx`, `sine_st8_v2.0_audio.sqx` | 2.0 | 5 (audio) | 8-bit mono and stereo, written by Squeez-c's own `sqx create --fmt=20 --method=5` |
| `x_altst16_v2.0_audio.sqx`, `x_5003st16_v2.0_audio.sqx`, `x_ramp16_v2.0_audio.sqx` | 2.0 | 5 (audio) | 16-bit stereo and mono, written by Squeez-c's own encoder |

The three `Noise_*`/`test_*` archives are from Squeez-c's `corpus` (archives the original SQX
archiver wrote), with their WAV files; the other audio sources are in the same `corpus`, except
`sine_st8.wav`, a generated 8-bit stereo sine pair (24044 bytes, SHA-256 `8ad1101a…2fa1`).

Encrypted archives (`pw.*`, kept out of the password-less round of tests), written by Squeez
CommandLine 5.2 for Linux (`sqc`, from the SQX SDK mirror https://github.com/wesinator/sqx-compression,
`sqc_linux.tgz`) with `sqc a -ep -fmt SQX2|SQX1 [-m0|-s -mx] -p secret [-ph secret]` over five files
(`a.bin` 0..255 four times, `b.txt` a repeated sentence, `c.bin` 5000 random bytes, `d.bin`
`0123456789abcdef`, `e.bin` `x`):

| File | Format | Contents |
|---|---|---|
| `pw.enc_v2.0_stored.sqx` | 2.0 | stored, AES |
| `pw.enc_v2.0_normal.sqx`, `pw.enc_v1.1_normal.sqx` | 2.0, 1.1 | normal, AES |
| `pw.enc_v2.0_solid_max.sqx` | 2.0 | solid maximum, AES |
| `pw.enchdr_v2.0.sqx` | 2.0 | AES files and Blowfish headers (`-ph secret`) |
| `pw.encutf8_v2.0.sqx` | 2.0 | `b.txt` and `c.bin` with the password `pässwort` (UTF-8) |
| `pw.sdk_EncryptedArchive.sqx` | 1.1 | the SQX SDK 2.06's `Archives/EncryptedArchive.sqx` (password `test`), unchanged |

`volumes/mvol.sqx`, `mvol.001`, `mvol.002`: a multi-volume set Squeez CommandLine 5.2 wrote with
`sqc a -ep -fmt SQX2 -m0 -mv 140000` from `text.txt` (3000 lines `line i of the volume test`),
`huge.bin` (200000 random bytes, split over all three volumes) and `small.bin` (`tiny file`).

`recovery/`: `a3000_1.sqx` and `a60000_3.sqx` with their external records (`.sqx.sqr`), written by
Squeez CommandLine 5.2 with `sqc a -ep -fmt SQX2 -m0 -rr1|-rr3 -rre` from 3000 and 60000 random bytes;
`sdk_DamagedArchive.sqx`, the SQX SDK 2.06's `Archives/DamagedArchive.sqx` (an internal recovery
record and a damaged file), unchanged.

The `*_ultra.sqx` archives come from the same `packed_corpus` at revision
`03211cd1e86473da08bc675198f6902e15a28ca3`; `records.bin` (240000 bytes) is in its `corpus`.
