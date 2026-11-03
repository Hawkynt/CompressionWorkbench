# ZIP reference archives from libarchive

Taken from libarchive's test suite (`libarchive/test/*.uu` at tag `v3.7.7`, BSD-2-Clause,
Copyright (c) 2003-2023 Tim Kientzle and the libarchive contributors), uudecoded. The manifest in
`ZipLzmaReferenceVectorTests` lists each member's size and SHA-256 as 7-Zip 26.04 extracts it.

| File | Upstream name | What it covers |
|------|---------------|----------------|
| `test_read_format_zip_lzma.zipx` | same | method 14 (LZMA), general-purpose bit 1 clear: no end marker |
| `test_read_format_zip_lzma_multi.zipx` | same | three LZMA members, no end marker |
| `test_read_format_zip_lzma_stream_end.zipx` | same | method 14 with bit 1 set: the stream ends with an end marker |
