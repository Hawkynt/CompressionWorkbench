# LZAV reference vectors

These streams were written by `lzav_compress_default` from Aleksey Vaneev's LZAV 5.20, `avaneev/lzav` at `b93c884dab90ac16112f78daed9ff6e07c3d2fac` (MIT), built with gcc 16.2 for x86-64. `lzav_decompress` restored every stream to its input.

The file name is `<kind>-<length>.lzav`. The inputs come from the xorshift32 generator in `LzavReferenceVectorTests.Generate`. Inputs of 300000 bytes are above LZAV's 256 KiB threshold, so they use a minimum reference length of 6; smaller inputs use 5.

Our compressor must reproduce every stream byte for byte, and our decompressor must restore it.
