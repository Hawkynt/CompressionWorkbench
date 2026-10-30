# ZIP Shrink / Reduce / Implode reference data

Two sources, both public domain.

## PKZIP streams (`pkzip*.bin`)

Raw compressed member data that PKZIP itself wrote, taken from the test suite of hwzip 2.4
(<https://www.hanshq.net/zip2.html>, `hwzip-2.4.zip`, public domain, Hans Wennborg). There they
appear as C arrays, with the commands that made them: PKZIP 1.10 (`pkz110.exe`) or PKZIP 0.92
(`pkz092.exe`) from cd.textfiles.com, run in DOSBox over a prefix of Shakespeare's Hamlet.

| File | Made with | Uncompressed |
|------|-----------|--------------|
| `pkzip110-implode-4k-nolit-hamlet256.bin` | PKZIP 1.10 `pkzip -ei` | first 256 bytes of hamlet.txt; 4 KB window, no literal tree |
| `pkzip110-implode-8k-lit-hamlet8192.bin` | PKZIP 1.10 `pkzip -ei` | first 8192 bytes; 8 KB window, literal tree |
| `pkzip092-reduce4-hamlet2048.bin` | PKZIP 0.92 `pkzip -ea4` | first 2048 bytes; Reduce factor 4 |
| `pkzip092-reduce4-hamlet2048-zeros1024.bin` | PKZIP 0.92 `pkzip -ea4` | first 2048 bytes and 1024 zero bytes |
| `pkzip110-shrink-lzwfig5.bin` | PKZIP 1.10 `pkzip -es` | `ababcbababaaaaaaa` |

The tests check SHA-256 of the expected plaintext rather than carrying Hamlet.

## hwzip archives (`hwzip-*.zip`)

Written by `hwzip create -m shrink|reduce|implode` (hwzip 2.4, built from source) over three
inputs that `CodecProofData` regenerates: `text.txt` = `Text(60000)`, `mixed.bin` = `Mixed(30000)`
and `zeros.bin` = 20000 zero bytes. hwzip stores a member when a method does not shrink it. 7-Zip
26.04 tests the Shrink and Implode archives as OK; it has no Reduce decoder.
