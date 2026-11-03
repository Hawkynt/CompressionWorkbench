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

## Uninstallers

The `-uninst` installers add `WriteUninstaller "$INSTDIR\uninst.exe"` and an uninstall section
(`nsis3-stored-uninst.exe` is `SetCompress off`, so every block is stored). Their rows in
`oracle.tsv` come from 7-Zip 23.01 (x64, Linux) `7z x`. Each `uninst.exe` there is also
byte-identical to the file the installer itself writes when run silently
(`<installer> /S /D=<dir>`), so the expected bytes are what NSIS produces, not only what 7-Zip
reconstructs.

## 7-Zip 4.42's own installer

`7z442.exe` is the unmodified 7-Zip 4.42 installer, downloaded from
https://sourceforge.net/projects/sevenzip/files/7-Zip/4.42/7z442.exe/download
(SHA-256 `d950e6b9a1feab64bf9004355d43b58c90a396a1a54374341eba33592b3e5ee6`). 7-Zip's installers of that era are
built with an NSIS whose solid LZMA stream is preceded by a filter-flag byte; flag 1 means the
decoded stream went through the x86 branch filter (7-Zip lists the method as `BCJ LZMA:22`). The
installer also writes `Uninstall.exe` and installs `7-zip.dll` through `Library.nsh`'s `InstallLib`
macro (`File /oname=$R0`). Its rows in `oracle.tsv` come from 7-Zip 23.01 `7z x`.

7-Zip is GNU LGPL software (with the unRAR restriction on its RAR decoder); the installer is
redistributed unchanged.
