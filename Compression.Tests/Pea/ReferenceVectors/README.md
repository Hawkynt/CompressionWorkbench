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

Volume sets, from `/tmp/a2e1_peasrc` (the same four objects) with
`pea PEA <out> <volume size> <compression> <volume check> <object check> <stream check> HIDDEN FROMCL …`:

| Files | Volume size | Compression | Volume | Object | Stream |
| --- | --- | --- | --- | --- | --- |
| `vol.000001.pea` … `vol.000003.pea` | 5000 | PCOMPRESS0 | CRC32 | CRC32 | SHA256 |
| `volz.000001.pea` … `volz.000003.pea` | 200 | PCOMPRESS2 | SHA1 | MD5 | SHA256 |

Encrypted streams, password `password`, from the same four objects with
`pea PEA <out> 0 <compression> CRC32 <object check> <stream encryption> HIDDEN password NOKEYFILE FROMCL …`:

| File | Compression | Object | Stream encryption |
| --- | --- | --- | --- |
| `eax256.pea` | PCOMPRESS2 | CRC32 | EAX256 |
| `tf256.pea` | PCOMPRESS0 | CRC32 | TF256 |
| `sp256.pea` | PCOMPRESS1 | CRC64 | SP256 |
| `eax.pea` | PCOMPRESS2 | CRC32 | EAX |
| `tf.pea` | PCOMPRESS2 | CRC32 | TF |
| `sp.pea` | PCOMPRESS2 | CRC32 | SP |
| `hmac.pea` | PCOMPRESS2 | CRC32 | HMAC |

Triple cascades, password `password`, the same command with volume and object check CRC32 and the
cascade name (an appended digit is the strength, stored in archive header byte 9):

| File | Compression | Stream encryption | pea's time / peak memory |
| --- | --- | --- | --- |
| `triats.pea` | PCOMPRESS2 | TRIATS | 1.4 s / 28 MB |
| `tritsa.pea` | PCOMPRESS0 | TRITSA | 1.3 s / 27 MB |
| `trisat.pea` | PCOMPRESS1 | TRISAT | 1.3 s / 28 MB |
| `sriats.pea`, `sritsa.pea`, `srisat.pea` | PCOMPRESS2 | SRIATS, SRITSA, SRISAT | 1.6 s / 92 MB |
| `hriats.pea`, `hritsa.pea`, `hrisat.pea` | PCOMPRESS2 | HRIATS, HRITSA, HRISAT | 1.5 s / 92 MB |
| `triats1.pea` | PCOMPRESS2 | TRIATS1 | 2.9 s / 28 MB |
| `hritsa1.pea` | PCOMPRESS2 | HRITSA1 | 2.9 s / 158 MB |
| `sriats7.pea` | PCOMPRESS2 | SRIATS7 | 91 s / 1.05 GB |
| `hrisat7.pea` | PCOMPRESS2 | HRISAT7 | 114 s / 1.05 GB |

The two strength-7 files are read only by the explicit test
`GivenStrength7CascadeWrittenByPeaZip_WhenRead_ThenItDecrypts`. Recorded runs, 2026-11-20 (Windows
11, .NET 10): both decrypt byte-exact. `sriats7.pea` (three scrypt runs with N = 1 Mi, r = 8, p = 8
over a 1 GiB table) took 7 min 36 s at first, 2 min 1 s (Debug) and 44 s (Release) with the unrolled
scrypt; `hrisat7.pea` 4 min 24 s, then 2 min 37 s (Debug) and 1 min (Release). pea: 91 s and 114 s.

`pea UNPEA <archive> <dir>/ RESETDATE RESETATTR EXTRACT2DIR HIDDEN "" ""` extracts them under
`<dir>/<archive name>/`, relative to the objects' common directory.
