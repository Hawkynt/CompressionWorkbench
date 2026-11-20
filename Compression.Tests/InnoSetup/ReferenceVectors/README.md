# Inno Setup reference installers

Real Inno Setup installers, compiled by the official Inno Setup compilers (never by
CompressionWorkbench):

| Compiler | Source |
| --- | --- |
| `ISCC.exe` 5.6.1 (ANSI) | `innosetup-5.6.1.exe` from https://files.jrsoftware.org/is/5/ |
| `ISCC.exe` 5.6.1 (Unicode) | `innosetup-5.6.1-unicode.exe` from https://files.jrsoftware.org/is/5/ |
| `ISCC.exe` 6.0.5 | `innosetup-6.0.5.exe` from https://github.com/jrsoftware/issrc/releases/tag/is-6_0_5 |
| `ISCC.exe` 7.1.1 | `innosetup-7.1.1-x64.exe` (SHA-256 `8a5a6711c3766a6e5fb74f280ba0e3551e719ce94538d901fdbd1f8f9920b3f0`) from https://github.com/jrsoftware/issrc/releases/tag/is-7_1_1 |

The compilers were taken out of those distributions without running the installers: up to 6.0.5
with innoextract 1.9; 7.1.1, which no innoextract reads, with this repository's reader, every one
of its 136 files matching the SHA-256 the installer stores. `make-fixtures.py` writes the payload (two copies of a short text, 3000 seeded random
bytes, 32000 bytes of a repeated pattern) and one script per variant, then runs the compiler:
`python make-fixtures.py <workdir> <tag> <ISCC.exe> zip lzma2:solid ...`. File names say compiler
version (`a` ANSI, `u` Unicode), `Compression=` and `SolidCompression=yes`. Each installer is stored
gzip-compressed and otherwise unchanged.

`oracle.tsv` is what innoextract 1.9 extracts from each installer (`innoextract -e`): path, size
and SHA-256 per file. The tests require our reader to produce exactly that set. For
`inno711-lzma2-solid.exe` (7.1.1, beyond any innoextract) the rows are the payload compiled in.

**Encrypted installers** (`*-enc*`), compiled by `make-encrypted.py` from the same payload with
`Password=` and `Encryption=yes` (`Encryption=full` for `inno654-encfull-zip.exe`, which encrypts
the setup headers too):

| Fixture | Compiler | Cipher | Password |
| --- | --- | --- | --- |
| `inno538a-enc-zip.exe` | `ISCC.exe` 5.3.8 (ANSI), `isetup-5.3.8.exe` from https://files.jrsoftware.org/is/5/ | ARC4, MD5 key | `Inno-Secret_1` |
| `inno561u-enc-lzma2-solid.exe` | `ISCC.exe` 5.6.1 (Unicode) | ARC4, SHA-1 key | `Pässwört-€1` |
| `inno643-enc-zip.exe` | `ISCC.exe` 6.4.3, `innosetup-6.4.3.exe` from https://github.com/jrsoftware/issrc/releases/tag/is-6_4_3 | XChaCha20, PBKDF2-SHA256 key | `Pässwört-€1` |
| `inno654-encfull-zip.exe` | `ISCC.exe` 6.5.4, `innosetup-6.5.4.exe` from https://github.com/jrsoftware/issrc/releases/tag/is-6_5_4 | XChaCha20, headers too | `Pässwört-€1` |
| `inno711-enc-zip.exe` | `ISCC.exe` 7.1.1 | XChaCha20, PBKDF2-SHA256 key | `Pässwört-€1` |
| `inno711-encfull-lzma2-solid.exe` | `ISCC.exe` 7.1.1 | XChaCha20, headers too | `Pässwört-€1` |

Compilers before 6.4 encrypt through `ISCrypt.dll` (2560 bytes, SHA-256
`2f6294f9aa09f59a574b5dcd33be54e16b39377984f3d5658cda44950fa0f8fc`, from
https://jrsoftware.org/download.php/iscrypt.dll), placed beside `ISCC.exe`. The 5.3.8 compiler was
taken out of its distribution with innoextract 1.9; the 6.4.3, 6.5.4 and 7.1.1 compilers, newer than any
innoextract, with this repository's reader, every file matching the SHA-256 its installer stores.
The oracle rows for the 5.x fixtures are innoextract 1.9 with `--password`, which also refuses a
wrong password; no innoextract reads 6.4 or later, so their rows are the payload compiled in.

Inno Setup is distributed under the Inno Setup License (Jordan Russell, Martijn Laan): permissive,
retaining its copyright notices; the installers contain only its setup loader, setup program and
the payload above.

Not embedded (too large), checked locally with the opt-in tests in `InnoSetupReferenceVectorTests`:
the official `innosetup-5.6.1.exe`, `innosetup-5.6.1-unicode.exe` and `innosetup-6.0.5.exe` match
innoextract 1.9 file for file (`CWB_INNO_FIXTURES`); `innosetup-6.4.3.exe` (103 files) and
`innosetup-7.1.0-x86.exe` (137 files), newer than any innoextract, extract with every file matching
the SHA-256 the compiler stored for it (`CWB_INNO_SELFCHECK`).

**16-bit installer** (`inno1216-16bit.exe`): Inno Setup 1.2.16 for Windows 3.x, whose setup data
carries the 12-byte tag `i1.2.10--16`. The compiler, `COMPIL16.EXE`, was taken with innoextract
out of `isetup16-1.2.16.exe` from https://files.jrsoftware.org/is/1.2/ (SHA-256
`9f4fbab8039f40cd3ea6dcbe34de0d4d40ed4037bc1c9737027b20181e461dc5`) and run under wine 9.0 as
`COMPIL16.EXE /cc PROBE.ISS`. The script is `make-16bit.sh`. It has `Bits=16` and one entry in
each of [Dirs], [Files], [Icons], [INI], [Registry], [Run] and [UninstallDelete], so every 16-bit
record layout is parsed. The payload is generated: 21643 bytes of seeded words, 3000 seeded random
bytes, an empty file, and a 16-byte file in a subdirectory. The `oracle.tsv` rows are what
innoextract 1.10-dev (`6e9e34e`) extracts. They equal the generated inputs.

**Writer headers** (`iscc-headers/`): what the writer must reproduce. `make-iscc-headers.sh`
compiles `ref.iss` (two files, `Compression=none`, `TimeStampsInUTC=yes`, the release's own
wizard bitmaps) with the ISCC of one installed release per setup data id (6.0.5, 6.1.2, 6.2.2,
6.3.3, 6.4.1, 6.4.2, 6.4.3, 6.5.1, 6.5.4, 6.6.0, 6.6.1, 6.7.4, 7.1.0 and 7.1.1, each from its
official installer at https://github.com/jrsoftware/issrc/releases, installed under wine 9.0 with
`/VERYSILENT /DIR=C:\IS<version>`), and `iscc-headers.py` prints the SHA-256 of both decompressed
setup-0 blocks. `InnoSetupWriterVersionTests` holds those hashes and requires the writer, given the
same release's stubs (`CWB_INNO_STUBS_ROOT`), to produce exactly those blocks.
