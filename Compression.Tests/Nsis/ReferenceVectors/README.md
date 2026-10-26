# NSIS reference installers

Real NSIS installers, compiled by the official NSIS compilers, never by CompressionWorkbench:

| Compiler | Source |
| --- | --- |
| `makensis.exe` 3.10 | `nsis-3.10.zip` from https://sourceforge.net/projects/nsis/files/NSIS%203/3.10/ |
| `makensis.exe` 2.51 | `nsis-2.51.zip` from https://sourceforge.net/projects/nsis/files/NSIS%202/2.51/ |

`make-fixtures.py` writes the payload (two copies of a short text, 3000 seeded random bytes so that
Deflate emits stored blocks, 32000 bytes of a repeated pattern) and one script per variant, then runs
the compiler: `python make-fixtures.py <workdir> <makensis-3.10> <makensis-2.51>`. The file name says
compiler major version, `SetCompressor` and `/SOLID`; `-ansi` is `Unicode false` under NSIS 3 (NSIS 2
is always ANSI). Each installer is stored gzip-compressed; nothing else was changed.

`oracle.tsv` is what 7-Zip 26.03 (x64) extracts from each installer (`7z x`): path, size and SHA-256
per file. The tests require our reader to produce exactly that set.

NSIS is released under the zlib/libpng licence; the installers contain only the NSIS exehead stub
and the payload above.
