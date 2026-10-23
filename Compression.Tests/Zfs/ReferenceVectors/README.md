# ZFS reference pools

Vdev images that OpenZFS created, gzip-compressed (`gzip -9 -n`) only to keep the
repository small; the tests decompress them in memory. CompressionWorkbench wrote none of
these bytes.

| | |
| --- | --- |
| Written by | OpenZFS `zfs-2.2.2-0ubuntu9.5` / `zfs-kmod-2.2.2-0ubuntu9.5` (Ubuntu 24.04 kernel `6.8.0-146-generic`) inside the libguestfs appliance |
| Recipe | `generate.sh` (host side) runs `generate-inner.sh` in the appliance |
| Created | 2026-10-19 |
| Verified | `zpool scrub -w` of every pool reported "repaired 0B ... with 0 errors" before `zpool export` |
| Manifests | `<pool>.manifest`, taken from the mounted pool before export: `sha256 size path` for files, `L path -> target` for symlinks, `D path` for directories |

Random file contents come from `/dev/urandom`, so a regeneration changes the images and
the manifests together; the tests only ever compare the two.

## Pools

| Pool | Devices | What it covers |
| --- | --- | --- |
| `cwbmain` | 1 × 128 MiB, ashift 12, pool version 5000 | datasets with compression `off`, `lzjb`, `zle`, `gzip-1`, `gzip-9`, `lz4`, `zstd`, `zstd-19`, `zstd-fast`; checksums `fletcher2`, `sha256`, `sha512`, `skein`, `edonr`, `blake3`; `recordsize=4K` with a three-level sparse file and `recordsize=1M`; `dnodesize=auto` with `xattr=sa`; `copies=2`; `dedup=on`; a ZPL version 4 dataset; nested datasets; a 600-entry fat-ZAP directory; snapshot `c-lz4@before` taken before a file changed and another was deleted |
| `cwbv28` | 1 × 64 MiB, ashift 9, pool version 28 (no feature flags) | `compression=on` (lzjb), a ZPL version 3 dataset (`znode_phys_t` bonus) with gzip |
| `cwbmirror` | 2 × 64 MiB, mirror | either side alone holds the pool |
| `cwbraidz1` | 3 × 64 MiB, RAID-Z1, ashift 9, compression off | a 1.5 MB file crossing the 1 MiB parity-swap boundary; any one device can be missing |
| `cwbraidz2` | 4 × 64 MiB, RAID-Z2, ashift 12 | double parity |
| `cwbstripe` | 2 × 64 MiB, two top-level vdevs | blocks on both devices |
| `cwbgang` | 1 × 64 MiB | written with `metaslab_force_ganging=16384` and `metaslab_force_ganging_pct=100`, so its data blocks are gang blocks |
| `cwbenc` | 1 × 64 MiB | dataset `secret` with `encryption=aes-256-gcm` (passphrase `cwb-passphrase`) beside a plain file; the reader must refuse the dataset, not guess |

## Files

| File | Bytes | SHA-256 |
| --- | ---: | --- |
| `cwbenc.img.gz` | 246606 | `9b8a896b91fce3345697945e60d86dbb39dd404ede8ca19ab41980a97bf5eef4` |
| `cwbgang.img.gz` | 461623 | `06b303498f65d611ee07a7a966d44592a58a7c15a932585388a1529f5aba751e` |
| `cwbmain.img.gz` | 3671133 | `289ffa2ac9fddcf47d57201786e1b0724a855946865005fe4f0bdb180fcf6727` |
| `cwbmirror-0.img.gz` | 407088 | `24ebdc79e397aaa401bbad6a8a24cd5df001e181fbb56b32b273741e82cefd2e` |
| `cwbmirror-1.img.gz` | 407078 | `53427c8d96ad5203534f4838935b0c7bc088ad0707e940c57c153e29ab593aac` |
| `cwbraidz1-0.img.gz` | 299674 | `a10f6de97bbadaa721c171927905470a9f442c12263d42c45c21635b006057a9` |
| `cwbraidz1-1.img.gz` | 318774 | `f0ade4974b243f75d8fd29e9e6f0b802db5f24ce86d7c01a5a0085cd8a7c664d` |
| `cwbraidz1-2.img.gz` | 312369 | `e7dd0bbe3e454bda5678d361954926ff7117a2f3f264a3cd972330632a61e6bb` |
| `cwbraidz2-0.img.gz` | 338417 | `0cbea9bdcda80c122086b68a294df2627d69f6dd8981c5131cf8da9353019f12` |
| `cwbraidz2-1.img.gz` | 323814 | `82ed25e2dc832fa35becc791c7a54dc0653b1e43d3c8cd071fbbd6ef64e65109` |
| `cwbraidz2-2.img.gz` | 276370 | `f10e7592ffe1a8464ba235593ee02b69ec631b6678a3dcb6b39971d0e2480534` |
| `cwbraidz2-3.img.gz` | 284148 | `02b173595a7115839bf43f489a4375968c7566c7df39e3ecdf19836e45808603` |
| `cwbstripe-0.img.gz` | 147073 | `6d1bc5e944171a66aac4c6d4990eee489bfbc6e59e00c1bf09aedbe523b39471` |
| `cwbstripe-1.img.gz` | 266443 | `17d5f5057d30ace136a02ee858b62247b19822f441dbd9cbb93b5f0548b23cf9` |
| `cwbv28.img.gz` | 402574 | `69c2d145da124f19c686aa3daf06158cc55cbbea46b8723fb911c72123e43c3b` |
