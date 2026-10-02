# HFS+ reference volume

`hfsplus.raw.gz` is keramics' HFS+ test volume, gzip-compressed (`gzip -9 -n`) only to keep the
repository small; the tests decompress it in memory. macOS wrote the volume itself, never
CompressionWorkbench.

| | |
| --- | --- |
| Upstream | [keramics/keramics](https://github.com/keramics/keramics) `test_data/hfs/hfsplus.raw` |
| Revision | `c0a985640f4a74a294ec95e080099774b2b5f908` (fetched 2026-10-01 from `raw.githubusercontent.com`) |
| Written by | `hdiutil` + the mounted volume on macOS, files made by `scripts/shared_macos.sh` (`create_file_entries`), compressed ones by `afsctool -c -T ZLIB/LZVN/LZFSE` |
| `hfsplus.raw` | 4153344 bytes, git blob `ff9800c0358067c65871a1754bc5c542e9d7b5fe` (same as upstream), SHA-256 `9e5bbcbc64b44d9b22b192d9ab90f2f65e39360f191d0fc93922627262c2e771` |
| `hfsplus.raw.gz` | 26735 bytes, SHA-256 `b082e9c03cb69564032305e524b306a07578bbdf31c49b6ba134a677ba798112` |

## Expected values

The six files that HFS+ transparent compression stored as a decmpfs attribute plus, for the
chunked methods, a resource fork. libfshfs 20260922 (`pyfshfs`) decodes them to:

| File | decmpfs method | Size | SHA-256 |
| --- | --- | --- | --- |
| `testdir1/compressed1` | 3 (zlib, inline) | 19 | `a78f2707f267bc43180b87ad4f4834996e9adb55d03e6cb84b888c58ecc1cc5f` |
| `testdir1/compressed2` | 4 (zlib, resource fork) | 11358 | `cfc7749b96f63bd31c3c42b5c471bf756814053e847c10f3eb003417bc523d30` |
| `testdir1/compressed3` | 7 (LZVN, inline) | 19 | as `compressed1` |
| `testdir1/compressed4` | 8 (LZVN, resource fork) | 11358 | as `compressed2` |
| `testdir1/compressed5` | 11 (LZFSE, inline) | 19 | as `compressed1` |
| `testdir1/compressed6` | 12 (LZFSE, resource fork) | 11358 | as `compressed2` |

The short files hold `My compressed file` and a newline. The long ones hold the Apache License
2.0 text, as does the uncompressed `testdir1/TestFile2`.

## Licence

keramics: Apache License 2.0, Copyright 2024-2026 Joachim Metz
(https://www.apache.org/licenses/LICENSE-2.0). The repository ships no NOTICE file.
