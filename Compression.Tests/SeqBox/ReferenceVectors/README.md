# SeqBox reference containers

Every file here was written by the reference encoder `sbxenc.py` 1.0.2 from
[MarcoPon/SeqBox](https://github.com/MarcoPon/SeqBox) at revision
`dee472acfe89f08b7112ce854ca98f2094fbae3b`, run with Python 3.12 on Ubuntu 24.04 (WSL), never by
CompressionWorkbench.

Sources: `fox.txt` = `The quick brown fox jumps over the lazy dog. ` × 50 (2250 bytes), `noise.bin` =
the 1000 bytes `(i * 31 + 7) mod 256`, `empty.bin` = 0 bytes; all with mtime 2001-02-03 04:05:06 UTC.

| File | Command | What it pins |
| --- | --- | --- |
| `fox-v1.sbx` | `sbxenc.py -uid 0000c0ffee01 fox.txt fox-v1.sbx` | version 1 (512-byte blocks), full metadata block |
| `noise-v2.sbx` | `sbxenc.py -uid 0000c0ffee02 -sv 2 noise.bin noise-v2.sbx` | version 2 (128-byte blocks) |
| `fox-v3.sbx` | `sbxenc.py -uid 0000c0ffee03 -sv 3 fox.txt fox-v3.sbx` | version 3 (4096-byte blocks) |
| `noise-v4.sbx` | `sbxenc.py -uid 0000c0ffee04 -sv 4 noise.bin noise-v4.sbx` | version 4 (488-byte blocks) |
| `fox-nometa-v1.sbx` | `sbxenc.py -uid 0000c0ffee05 -nm fox.txt fox-nometa-v1.sbx` | no metadata block: payload keeps the 0x1A padding |
| `empty-v1.sbx` | `sbxenc.py -uid 0000c0ffee06 empty.bin empty-v1.sbx` | metadata block only |

`sbxdec.py -i fox-v1.sbx` reports the expected name, size (2250) and SHA-256.
