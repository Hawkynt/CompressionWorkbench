# LZFSE reference vectors

Streams written by Apple's lzfse encoder. They are the test data for `LzfseAppleReferenceVectorTests`.

| | |
|---|---|
| Written by | `liblzfse.compress` from pyliblzfse 0.4.1, a Python build of Apple's lzfse reference library (BSD-3-Clause), with default parameters |
| Inputs | deterministic generators (text, mixed text and random runs), plus zeros and a single `A` byte; only their size and SHA-256 are kept |
| Checked | `liblzfse.decompress` returned each input before the stream was saved |

`manifest.tsv` lists file, decoded size, SHA-256 of the decoded bytes and stream size.

| File | Blocks Apple chose |
|---|---|
| `apple-text-1m.lzfse` | 14 `bvx2` (entropy-coded), `bvx$` |
| `apple-text-3k.lzfse` | `bvxn` (LZVN), `bvx$` |
| `apple-mixed-20k.lzfse` | `bvx-` (stored), `bvx$` |
| `apple-zeros-100k.lzfse` | `bvx2`, `bvx$` |
| `apple-one-byte.lzfse` | `bvx-`, `bvx$` |
