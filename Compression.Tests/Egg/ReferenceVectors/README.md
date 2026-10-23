# EGG reference vectors

All files come unmodified from EggDotNet's test corpus (`src/EggDotNet.Tests/test_files/`,
akolman/EggDotNet master, MIT, fetched 2026-10-23); they were written by ALZip.

| File | Exercises |
| --- | --- |
| `azoshort_txt.egg`, `azonumbers.egg` | AZO compression |
| `lzma_simple.egg` | LZMA (34 446 bytes in 5 723) |
| `solid.egg` | solid mode (first and last member identical) |
| `lorem_long_zipEnc.egg`, `lorem_long_aes128.egg`, `lorem_long_aes256.egg` | ZIP 2.0, AES-128 and AES-256 encryption (password `password12345!`) |
| `lorem_long_store_lea128.egg`, `lorem_long_store_lea256.egg` | LEA-128 and LEA-256 encryption (same password) |
| `small_kor.egg`, `small_shift.egg` | CP949 and Shift-JIS file names |
| `directories.egg` | nested directories |
| `zeros.egg` | a 197 540 460-byte member (listed, not extracted) |
| `posix_small.egg`, `globalcomment.egg` | POSIX file information, archive comment |

Each data block stores the CRC-32 ALZip computed, and the reader checks it, so every member these
tests extract is byte-exact; the content assertions repeat EggDotNet's own.

`ours.egg` is our writer's output (`docs/text.txt`, an empty `empty.txt`, `raw.bin`; Deflate).
EggDotNet (same revision, a net10.0 console that opens each member and checks its CRC) read it
back as recorded in `eggdotnet-listing.txt`. The empty member has no block, as ALZip writes it, so
EggDotNet lists it without opening it.
