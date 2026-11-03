# LZF reference vectors

`lzf_compress` from Marc Lehmann's liblzf 3.6 (`dist.schmorp.de/liblzf/liblzf-3.6.tar.gz`, BSD-2-Clause) wrote these streams. It was built with gcc 16.2 using liblzf's default configuration (`HLOG` 16, `VERY_FAST`) and `INIT_HTAB=1`, so that match finding does not depend on uninitialised memory. `lzf_decompress` restored every stream to its input.

Each input comes from the xorshift32 generator in `LzfLiblzfVectorTests.Generate`. The file name is `<kind>-<length>.lzf`.

Our compressor must reproduce these files byte for byte, and our decompressor must restore them.
