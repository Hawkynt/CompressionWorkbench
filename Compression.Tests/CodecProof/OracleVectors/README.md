# Oracle vectors for the codec proof ledger

Third-party encoders wrote these streams. `OracleVectorTests` decodes each one with our building block, or with the format that has that id (`fmt.<Id>`), and compares the result with the sample it was made from. The ledger `docs/CODEC-PROOF-AUDIT.md` lists which codecs these pin.

## Samples

- `sample-text.bin`: 24000 bytes from the text generator in `CodecProofData`. SHA-256 `1dbe30f5…85ec4e`.
- `sample-mixed.bin`: 6000 bytes of mixed text and random runs. SHA-256 `56c728c1…0a7d68`.
- `sample-small.bin`: `abracadabra abracadabra` (23 bytes).

## Framing

Most of our blocks put a 4-byte little-endian uncompressed length in front of the codec's own stream. Where that applies, the length was prepended to the third-party stream. Nothing else was changed.

## Producers

`manifest.tsv` names the producer of each file. The command-line tools were built with gcc 16.2 (MinGW-w64) from these revisions:

| Library | Revision | Licence |
|---|---|---|
| jibsen/brieflz (`blzpack`) | `ad89acf4186adca8ad8c27c4ab376a49ae233cd0` | zlib |
| mbitsnbites/liblzg (`lzg`) | `c614d57fb5ba19681855df554b59826f1e1ef309` | zlib |
| inikep/lizard (`lizard` 2.1.0) | `d3becc7e80d3eb01147c2f63bed2e23fac533f19` | BSD-2-Clause / GPL-2.0 (CLI) |
| johnezang/pithy | `d7d5bd3a20f97d46454f9e651ec6b3dd5801885e` | BSD-3-Clause |
| einar-saukas/ZX0 (2.2, `-c` classic format) | `ecde3a2ae05061fe06469ed46df81a33b7de7d86` | BSD-3-Clause |
| emmanuel-marty/salvador (1.4.2) | `1662b625a8dcd6f3f7e3491c88840611776533f5` | zlib / CC0 |
| emmanuel-marty/apultra (1.4.8, aPLib-compatible) | `8f340057d7402c10da3d9c76c599f9ab83b8a22d` | zlib / CC0 |
| UCL 1.03 (Markus Oberhumer), NRV2B/D/E with a 32-bit LE bit buffer | release tarball | GPL-2.0 (oracle only; no code taken) |

The Python oracles were brotli 1.2.1, lz4 4.4.5, zstandard 0.25.0, cramjam 2.14.0 (snappy raw format), ncompress 1.0.2, inflate64 1.0.4 and lzallright 0.3.0 (LZO1X-1). CPython's zlib, bz2 and lzma were used too.
