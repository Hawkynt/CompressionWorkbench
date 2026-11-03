# PEA reference archives

Written by PeaZip 11.4.0's `pea` (Linux GTK2 portable build, run headless under `xvfb-run` on
Ubuntu 24.04 / WSL), never by CompressionWorkbench.

Source tree `/tmp/peasrc`: `hello.txt` (`hello pea\n`), `fox.txt`
(`The quick brown fox jumps over the lazy dog.\n` × 300), `empty.txt` (0 bytes), `sub/n.txt`
(`nested\n`), all with mtime 2001-02-03 04:05:06 UTC; `big.bin` is the 3,000,000 bytes
`(i * 131 + (i >> 7)) mod 256`.

Every archive was made with
`pea PEA <out> 0 <compression> <volume check> <object check> <stream check> HIDDEN FROMCL /tmp/peasrc/hello.txt /tmp/peasrc/fox.txt /tmp/peasrc/empty.txt /tmp/peasrc/sub`:

| File | Compression | Volume | Object | Stream |
| --- | --- | --- | --- | --- |
| `store.pea` | PCOMPRESS0 | CRC32 | CRC32 | NOALGO |
| `deflate3.pea` | PCOMPRESS1 | ADLER32 | CRC64 | SHA256 |
| `deflate6.pea` | PCOMPRESS2 | CRC32 | CRC32 | SHA256 |
| `deflate9.pea` | PCOMPRESS3 | SHA1 | MD5 | SHA512 |
| `sha3.pea` | PCOMPRESS2 | SHA3_256 | SHA3_512 | BLAKE2B |
| `misc.pea` | PCOMPRESS2 | RIPEMD160 | WHIRLPOOL | BLAKE2S |
| `big.pea` | PCOMPRESS2 | CRC32 | CRC32 | SHA256 (input: `big.bin` only; three 1 MiB blocks) |

`pea UNPEA <archive> <dir>/ RESETDATE RESETATTR EXTRACT2DIR HIDDEN "" ""` extracts them under
`<dir>/<archive name>/`, relative to the objects' common directory.
