# XAR reference archives

Written by libarchive's `bsdtar 3.7.2` (libarchive 3.7.2, zlib 1.3, liblzma 5.4.5, bz2lib 1.0.8;
Ubuntu 24.04 package `libarchive-tools`) on 2026-10-19 with

```
bsdtar --format xar --options xar:compression=<c> -cf <c>.xar TEXT.TXT RANDOM.BIN REPEAT.BIN ONE.TXT
```

The inputs are the ones `Arj/ReferenceVectors/generate.sh` makes (same SHA-256s, listed in
`Arj/ReferenceVectors/README.md`).

| Archive | Encoding style in the TOC | SHA-256 |
|---------|---------------------------|---------|
| `none.xar` | `application/octet-stream` | `995a3e5c7f0acb4f02afb508527d47a7395cd8e36b087fdc504a57202333900c` |
| `gzip.xar` | `application/x-gzip` (a zlib stream) | `bec70f7f56a92d2dd3863f05ac3c843a201a5e09a1ae4c6259ac56416736244d` |
| `bzip2.xar` | `application/x-bzip2` | `1e1259563b7c82d5b74813d42ae6f86db2a277311037924c043881dfd88af841` |
| `lzma.xar` | `application/x-lzma` (libarchive writes the legacy `.lzma` format) | `d938ef9be3fc1bb70f24bc4401a8b63fe257a97cfee76b413e034188f3efac85` |
| `xz.xar` | `application/x-xz` | `667081a142509a9ca4810a4187cb511a5a8b83dfc0efae28ccf2b013592496a5` |

Every member carries the SHA-1 `extracted-checksum` bsdtar recorded, which the reader checks.
