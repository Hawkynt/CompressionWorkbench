# StuffIt X reference archives

The seven `.sitx` files come unmodified from Stephan Sokolow's
[StuffIt Test Files](https://github.com/ssokolow/stuffit-test-files), released under CC0 (see
`LICENSE.md`). StuffIt itself wrote each of them from the same small source files:

| File | Written by | Streams |
|---|---|---|
| `testfile.stuffit7_dlx.mac9.sitx` | StuffIt Deluxe 7, Mac OS 9 | Brimstone catalog, Brimstone + Cyanide forks, resource forks |
| `testfile.stuffit7_dlx.macx1.sitx` | StuffIt Deluxe 7, Mac OS X 10.1 | same, plus POSIX permissions |
| `testfile.stuffit_deluxe_2009.win.backcompat.sitx` | StuffIt Deluxe 2009, Windows, "backwards compatible" | Cyanide |
| `testfile.stuffit_deluxe_2009.win.basic.sitx` | StuffIt Deluxe 2009, Windows, "basic" | Iron |
| `testfile.stuffit_deluxe_2009.win.sitx` | StuffIt Deluxe 2009, Windows | Iron, method 7 (JPEG), RC4-obscured preview |
| `testfile.stuffit_deluxe_2010.win.basic.sitx` | StuffIt Deluxe 2010, Windows, "basic" | Iron |
| `testfile.stuffit_deluxe_2010.win.sitx` | StuffIt Deluxe 2010, Windows | Iron, method 7 (JPEG), RC4-obscured preview |

The expected SHA-256 values in `StuffItXTests` are those of the repository's source files: the
files at its top level and in `src2/`, with resource forks taken from the AppleDouble `._` files in
`src2/`. One exception: the Windows archives hold `testfile.txt` as `Testing 123\n` (LF rather
than the Mac CR), which is what unar extracts too. Every stream also carries a CRC-32 that the
reader checks.

The Unarchiver 1.10 (`unar`) extracts the five Windows archives, except the method-7 JPEG, which it
reports as not supported. It fails on both Mac archives: it reads the catalog's tag-10 name list
without the byte alignment the strings start on, and then stops at "unknown tag 63/23". The repo's
`recoverability` and `redundancy` archives are left out because unar cannot parse them either.
