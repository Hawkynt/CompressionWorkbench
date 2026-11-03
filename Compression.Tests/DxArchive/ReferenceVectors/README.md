# DxLib archive vectors

Made on 2026-11-03 by `mkfixtures.py` (documentation, not a build input) with the archivers DxLib
ships in `Tool/DXArchive`: **DxaEncode 1.10** from DxLib 3.25a (writes version 8) and
**DxaEncode 1.06** from DxLib 3.19b (writes version 6), both from <https://dxlib.xsrv.jp/>.
The inputs are generated deterministically (nested folders, an empty file, Shift-JIS text; the
file names are ASCII, because the archivers take names in the ANSI code page of the machine); `manifest.tsv` lists them, and every archive must extract to exactly those files.

| File | Made with | What it covers |
| --- | --- | --- |
| `v8-default.dxa` | `DxaEncode -NS` | default password, LZ, Huffman over the head and tail of known media types, compressed index |
| `v8-key.dxa` | `-K:Secret` | a password: archive key and per-file keys from it |
| `v8-nokey.dxa` | `-NK` | no encryption |
| `v8-stored.dxa` | `-NP` | no LZ |
| `v8-huffall.dxa` | `-H:255 -AH` | every file wholly Huffman-coded |
| `v8-huff1k.dxa` | `-H:1 -AH` | 1 KiB head and tail Huffman-coded, the middle stored after them |
| `v8-maxpress.dxa` | `-MP` | maximum LZ effort |
| `v6-default.dxa` | DxaEncode 1.06 | version 6, default key |
| `v6-key.dxa` | DxaEncode 1.06 `-K:Secret` | version 6 with a password (its key is also recovered from the header) |
