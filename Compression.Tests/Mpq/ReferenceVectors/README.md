# MPQ reference vectors

Third-party proof for the MPQ reader, writer, in-place editor and codecs. The oracle is
[StormLib](https://github.com/ladislav-zezula/StormLib) (MIT), revision
`116135c27e8c0dcf8ea0a0c549a6d737ce10fc73`, built from source with MinGW-w64 GCC 16.2
(`cmake -G "MinGW Makefiles" -DSTORM_USE_BUNDLED_LIBRARIES=ON`), driven by `mpqoracle.c` in this
folder (our own thin front end, compiled with `gcc -c mpqoracle.c -I<StormLib>/src` and linked
with `g++ mpqoracle.o -L<build> -lStormLib -lwininet -static`).

## Archives StormLib wrote

| File | Made by | What it covers |
| --- | --- | --- |
| `stormlib-v1.mpq` | `gen_fixtures.py` → `mpqoracle create … v1a …` | format v1 with `(listfile)` and `(attributes)`; PKWARE DCL, zlib, bzip2 + sector CRC, imploded, sparse, ADPCM mono/stereo + Huffman (`SFileAddWave` qualities 0–2), single-unit, stored, empty; encrypted files with and without the adjusted key |
| `stormlib-v2.mpq` | `gen_fixtures.py` → `mpqoracle create … v2 …` | format v2 header; LZMA, sparse + zlib, sparse + bzip2, zlib + sector CRC, PKWARE |

`*.manifest` lists, per archived name, the size, the block flags and the SHA-256 of the bytes
**StormLib itself extracts** (`manifest.py` → `mpqoracle extract`). The inputs are generated
deterministically by `make_inputs.py`. The ADPCM entries are lossy, so their hashes are StormLib's
decoding, not the input WAV.

Regenerate: `python make_inputs.py in`, `python gen_fixtures.py <mpqoracle.exe> in out`,
`python manifest.py <mpqoracle.exe> out/<archive>.mpq work > <archive>.manifest`.

## Archives our writer produced

`ours-*.mpq` are the writer's output for the profiles in `MpqStormLibOracleTests.WriterProfiles`
(stored, zlib, PKWARE, bzip2, LZMA (format v2), sparse + zlib, imploded + encrypted, zlib +
encrypted with the adjusted key). `ours-*.stormlib.txt` is `mpqoracle extract`'s listing for each:
StormLib opened the archive and read every file back to its input (`OK`). The tests pin the writer
to these bytes, so a change to the writer fails until it is re-proven: run the `ExternalInterop`
tests with `CWB_MPQ_ORACLE=<mpqoracle.exe>` and `CWB_MPQ_WRITE_VECTORS=1`.

## Live oracle tests

With `CWB_MPQ_ORACLE` set, `MpqStormLibOracleTests` also checks, against StormLib directly:
PKWARE DCL in both directions (binary and ASCII, 1/2/4 KiB dictionaries), Huffman for all nine
data types (identical streams), ADPCM mono/stereo + Huffman (identical streams and decodes),
sparse and LZMA sectors, and in-place add/replace/remove on `stormlib-v1.mpq` passing
`SFileVerifyFile` (CRC-32 and MD5 from the maintained `(attributes)`) and extracting byte for byte.
