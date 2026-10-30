# FastLZ reference vectors

These blocks were written by Ariya Hidayat's FastLZ (`ariya/FastLZ` at `b1342dabcf5257ab303743c9332fe75e9147a011`, MIT), using `fastlz_compress_level`. FastLZ was built with gcc 16.2 for x86-64. `fastlz_decompress` restored every block to its input.

The file name is `<kind>-<length>.level<n>.flz`. The input comes from the xorshift32 generator in `FastLzReferenceVectorTests.Generate`.

Our compressor must reproduce each block byte for byte at the same level, and our decompressor must restore it.
