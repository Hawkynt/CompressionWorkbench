# APFS reference volume

`apfs.raw.gz` is keramics' APFS test volume, gzip-compressed (`gzip -9 -n`) only to keep the
repository small; the tests decompress it in memory. macOS wrote the volume itself, never
CompressionWorkbench.

| | |
| --- | --- |
| Upstream | [keramics/keramics](https://github.com/keramics/keramics) `test_data/apfs/apfs.raw` |
| Revision | `4712c949645a1499635bc109692d4a9aedb9ba01` (fetched 2026-10-19 from `raw.githubusercontent.com`) |
| Written by | `hdiutil create -fs APFS` + the mounted volume on macOS (`scripts/generate_apfs_test_data_macos.sh`), files made by `scripts/shared_macos.sh` (`create_file_entries`), compressed ones by `afsctool -c -T ZLIB/LZVN/LZFSE` |
| `apfs.raw` | 4 153 344 bytes, SHA-256 `0bd1d94760df28a842b822d37a06750c739c9a472d7de2590a054d00795c63e9` |

## Expected values

The same `create_file_entries` made keramics' HFS+ volume (`HfsPlus/ReferenceVectors`), so the
compressed files hold the same bytes: `testdir1/compressed1`, `3` and `5` are
`My compressed file` and a newline (19 bytes, SHA-256
`a78f2707f267bc43180b87ad4f4834996e9adb55d03e6cb84b888c58ecc1cc5f`), `compressed2`, `4` and
`6` the Apache License 2.0 text (11 358 bytes, SHA-256
`cfc7749b96f63bd31c3c42b5c471bf756814053e847c10f3eb003417bc523d30`) — the bytes of the
uncompressed `testdir1/TestFile2` on the same volume.

## Encrypted volume (downloaded at test time)

`ApfsEncryptionTests` reads fox-it/dissect.apfs's `tests/_data/encrypted.bin.gz`
(https://github.com/fox-it/dissect.apfs, revision `a5626763922a16182a670e4906cfb8b88cf4baa6`,
SHA-256 `9780647470fd4a818c0c7ae86f70f2b7d39c48a5ccf2f89f0f2bd47d35915f1d`), a 4 MiB
container holding a FileVault volume whose password is `password`. The project is
AGPL-3.0, so the image is downloaded when the test runs and never committed; the test is
skipped without network access.

`dissect-encrypted.manifest` is what apfs-fuse (https://github.com/sgan81/apfs-fuse,
revision `66b86bd`), mounted with `-r password`, read from that volume: `f <sha256> <path>`
per file, `l <target> <path>` per symbolic link, `o - <path>` per device node or FIFO.

## Licence

keramics: Apache License 2.0, Copyright 2024-2026 Joachim Metz
(https://www.apache.org/licenses/LICENSE-2.0). The repository ships no NOTICE file.
