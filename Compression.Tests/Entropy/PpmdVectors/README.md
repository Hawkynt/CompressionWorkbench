# 7z PPMd reference vectors

## `libarchive-7zip-ppmd.7z`

`test_read_format_7zip_ppmd.7z.uu` from libarchive's test suite (`libarchive/test/`, master
348e5a2e27503f5bf114e298e2bd9ddcd59ab48e, BSD-2-Clause), uudecoded. It holds one file,
`ppmd_test.txt` (102400 bytes, SHA-256 `e587c5d5…3792136`), packed by 7-Zip with
`PPMD:o6:mem24`. Its packed stream starts right after the 32-byte signature header and is
10280 bytes long. 7-Zip 26.04 extracts it to the stated hash.

## Oracle hashes in `SevenZipPpmdOracleTests`

These are SHA-256 hashes of the streams that pyppmd 1.3.1 writes. pyppmd is a Python
binding of 7-Zip's `Ppmd7.c`/`Ppmd7Enc.c` and is what py7zr uses for 7z PPMd. Each
stream was made with `Ppmd7Encoder(order, mem).encode(data) + flush(endmark)` over the
test's own deterministic inputs. pyppmd's decoder read every one of them back.
