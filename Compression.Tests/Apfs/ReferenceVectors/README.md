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

## Snapshots (downloaded at test time)

`ApfsSnapshotTests` reads dissect.apfs's `tests/_data/snapshot.bin.gz` (same revision,
SHA-256 `63dedb8142c17e6ab6709c9fe2c37c695f1caa617946bc8d499087c5299dcf2b`): a 32 MiB
container whose volume "Snapshots" holds 512 snapshots, `file` reading `Snapshot i` and a
newline when snapshot i was taken. AGPL-3.0, so downloaded and never committed.

`dissect-snapshot.manifest` is what apfs-fuse (`66b86bd`) read from the live volume and,
mounted with `-o snap=<xid>`, from snapshots 1-3, every 100th and the last (`s <xid> <name>`
lines head each; `.fseventsd` entries kept only for the first three snapshots), listed
under `.snapshots/<name>/` the way the reader lists them; `n` gives the snapshot count.

## Clones and a kernel-written snapshot

`apfs-clones.img.gz` (64 MiB container, gzip-compressed) was formatted by mkapfs from
apfsprogs (`b9c4cb40`) and filled by the linux-apfs-rw kernel module (`6cf6de8a`) inside a
libguestfs appliance: two files, a whole-file clone of each (`cp --reflink=always`), a
symbolic link, a snapshot `before` (apfs-snap), then a write into the source, a write into
one clone and an append to the other. apfsck passes it. `apfs-clones.manifest` is what
apfs-fuse read from the live volume and the snapshot. `generate-clones.sh` documents the
steps. Captured 2026-11-23 on Ubuntu under WSL2 (appliance kernel 6.8.0-149-generic).

## Fusion container

`apfs-fusion-main.img.gz` is the 32 MiB main device of a Fusion container that
`mkapfs -F` (apfsprogs `b9c4cb40`) formatted over it and a 64 MiB tier-2 device; apfsck
checks the pair, and `apfs-fusion.manifest` is what apfs-fuse `-f` lists (an empty root).
Only the main device is kept: everything a fresh container reaches lies on it. No tool
outside macOS writes files into a Fusion container, so there is no sample with data on the
tier-2 device; the reader refuses such blocks. `generate-fusion.sh` documents the steps.

## Licence

keramics: Apache License 2.0, Copyright 2024-2026 Joachim Metz
(https://www.apache.org/licenses/LICENSE-2.0). The repository ships no NOTICE file.
