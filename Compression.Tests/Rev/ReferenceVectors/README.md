# RAR recovery volume reference vectors

Written on 2026-10-30 in WSL (Ubuntu 24.04) from `noise.bin` (9000 bytes of `new Random(12).NextBytes`):

| File | Origin |
| --- | --- |
| `r5-part1.rev`, `r5-part2.rev` | RAR 7.12 for Linux (`rarlinux-x64-712`, trial): `rar a -idq -m0 -ma5 -v3k -rv2 r5.rar noise.bin` wrote `r5.part1..4.rar` and these two volumes (`r5.part1.rev`, `r5.part2.rev`, renamed). zlib CRC32 of the data volumes: 3072 `cb2d4f55`, 3072 `23f14b00`, 3072 `34187c30`, 348 `8871cbc5`. |
| `r4-part1.rev`, `r4-part2.rev` | RAR 6.24 for Linux (`rarlinux-x64-624`; RAR 7 no longer writes RAR 4): `rar a -idq -m0 -ma4 -v3k -rv2 r4.rar noise.bin`, four data volumes, these two recovery volumes (renamed from `r4.part1.rev`, `r4.part2.rev`). |

The layouts the tests rely on are written down in `docs/REV-ON-DISK.md`.
