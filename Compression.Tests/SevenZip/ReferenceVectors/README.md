# 7z coder reference vectors

Single-member 7z archives written by third-party tools with coders the reader once refused.
`make_vectors.py` regenerates them and `manifest.txt` (size + SHA-256 of each member); the payloads
are derived deterministically by the script and are not checked in.

| File | Writer | Coder chain | Member |
| --- | --- | --- | --- |
| `deflate64.7z` | 7-Zip 26.03 (x64) | Deflate64 | `payload.bin` |
| `arm64-lzma2.7z` | 7-Zip 26.03 (x64) | ARM64 → LZMA2 | `payload.bin` |
| `riscv-lzma2.7z` | 7-Zip 26.03 (x64) | RISCV → LZMA2 | `payload.bin` |
| `zstd.7z` / `zstd-mt.7z` | 7-Zip 26.03 + 7-Zip-zstd 26.02 codec plugins (`Codecs-x64.7z`, release v26.02-v1.5.7-R2) | Zstandard, `-mmt=1` / `-mmt=4` | `payload.bin` / `big.bin` |
| `brotli.7z` / `brotli-mt.7z` | same | Brotli, `-mmt=1` / `-mmt=4` (one / two skippable-framed chunks) | `payload.bin` / `big.bin` |
| `lz4.7z` / `lz4-mt.7z` | same | LZ4, `-mmt=1` (bare frame) / `-mmt=4` (two skippable-framed chunks) | `payload.bin` / `big.bin` |
| `brotli-py7zr.7z` | py7zr 1.1.0 (brotli 1.2.0) | Brotli, one plain stream | `payload.bin` |

All archives were produced on Windows 11 on 2026-10-19 with `-ms=off -mhc=off`. 7-Zip's `t` command
(with the 7-Zip-zstd plugins) passes every 7-Zip-written one. The two Brotli framings are mutually
unreadable: 7-Zip-zstd rejects py7zr's plain stream and py7zr rejects 7-Zip-zstd's skippable-framed
chunks, so `brotli-py7zr.7z` is checked by py7zr reading it back (it does, SHA-256 matching the
manifest). The reader accepts both.
