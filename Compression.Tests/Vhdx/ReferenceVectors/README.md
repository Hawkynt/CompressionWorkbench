# VHDX reference disks

keramics' VHDX test disks, gzip-compressed (`gzip -9 -n`) only to keep the repository small;
the tests unpack them to a temporary directory, because a differencing disk is resolved
through its parent's file name.

| | |
| --- | --- |
| Upstream | [keramics/keramics](https://github.com/keramics/keramics) `test_data/vhdx/` |
| Revision | `4712c949645a1499635bc109692d4a9aedb9ba01` (fetched 2026-10-19 from `raw.githubusercontent.com`) |
| `ntfs-differential.vhdx` | 9 437 184 bytes, SHA-256 `65bd9e21c9f14d476df67b8fd104a61234ef693e70330c0e6b9878b10aff92ac` — differencing disk; its parent locator names `ntfs-parent.vhdx` |
| `ntfs-parent.vhdx` | 8 388 608 bytes, SHA-256 `4495375f1e92bb6ef1937f7cb4ccf56ca1c1c491c0df56280a8c1905a3d58d44` |
| `ntfs-dynamic.vhdx` | 37 748 736 bytes, SHA-256 `e02e32fbacdc0b02e5fe435b9527099627cc2c4d72e68f263d202bc9fa413763` |

## Expected values

`manifest.txt` holds the SHA-256 and length of each guest disk as libvhdi 20210425 read it
(Ubuntu's `python3-libvhdi`, the child opened with its parent attached). `qemu-img convert
-O raw` 8.2.2 agrees on the parent and the dynamic disk; it cannot open a differencing VHDX.
The differencing disk has two PARTIALLY_PRESENT blocks and one FULLY_PRESENT block, and
differs from its parent in 163 sectors. Captured 2026-10-23 on Ubuntu under WSL2.

## Licence

keramics: Apache License 2.0, Copyright 2024-2026 Joachim Metz
(https://www.apache.org/licenses/LICENSE-2.0). The repository ships no NOTICE file.
