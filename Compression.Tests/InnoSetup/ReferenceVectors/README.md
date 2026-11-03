# Inno Setup reference installers

Real Inno Setup installers, compiled by the official Inno Setup compilers (never by
CompressionWorkbench):

| Compiler | Source |
| --- | --- |
| `ISCC.exe` 5.6.1 (ANSI) | `innosetup-5.6.1.exe` from https://files.jrsoftware.org/is/5/ |
| `ISCC.exe` 5.6.1 (Unicode) | `innosetup-5.6.1-unicode.exe` from https://files.jrsoftware.org/is/5/ |
| `ISCC.exe` 6.0.5 | `innosetup-6.0.5.exe` from https://github.com/jrsoftware/issrc/releases/tag/is-6_0_5 |

The compilers were taken out of those distributions with innoextract 1.9, never by running the
installers. `make-fixtures.py` writes the payload (two copies of a short text, 3000 seeded random
bytes, 32000 bytes of a repeated pattern) and one script per variant, then runs the compiler:
`python make-fixtures.py <workdir> <tag> <ISCC.exe> zip lzma2:solid ...`. File names say compiler
version (`a` ANSI, `u` Unicode), `Compression=` and `SolidCompression=yes`. Each installer is stored
gzip-compressed and otherwise unchanged.

`oracle.tsv` is what innoextract 1.9 extracts from each installer (`innoextract -e`): path, size
and SHA-256 per file. The tests require our reader to produce exactly that set.

Inno Setup is distributed under the Inno Setup License (Jordan Russell, Martijn Laan): permissive,
retaining its copyright notices; the installers contain only its setup loader, setup program and
the payload above.

Not embedded (too large), checked locally with the opt-in tests in `InnoSetupReferenceVectorTests`:
the official `innosetup-5.6.1.exe`, `innosetup-5.6.1-unicode.exe` and `innosetup-6.0.5.exe` match
innoextract 1.9 file for file (`CWB_INNO_FIXTURES`); `innosetup-6.4.3.exe` (103 files) and
`innosetup-7.1.0-x86.exe` (137 files), newer than any innoextract, extract with every file matching
the SHA-256 the compiler stored for it (`CWB_INNO_SELFCHECK`).
