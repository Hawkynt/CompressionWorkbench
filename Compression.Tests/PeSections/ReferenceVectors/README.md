# PE section-view reference vectors

| File | Origin |
| --- | --- |
| `mingw-tiny.exe` | Linked by MinGW-w64 GCC 16.2.0 (WinLibs UCRT build) from `tiny.c` and the `windres`-compiled `tiny.rc`: `gcc -O1 -nostdlib -e start -s -Wl,--dynamicbase -o mingw-tiny.exe tiny.c tiny.res`. PE32+, seven sections including `.rsrc`. |
| `mingw-tiny-symbols.exe` | Same source without resources and without stripping: `gcc -O1 -g -nostdlib -e start -o mingw-tiny-symbols.exe tiny.c`. Carries DWARF sections whose long names live in the COFF string table (`/4`, `/19`, …) and a COFF symbol table. |
| `mingw-tiny-certificate-overlay.exe` | `mingw-tiny.exe` run through `addcert.py`: a 16-byte `WIN_CERTIFICATE` appended at an 8-byte boundary and referenced by data directory 4, then the 8-byte overlay `OVERLAY!`. |
| `7zip-listing.txt` | 7-Zip 26.04's view of each file (`7z l -slt -tPE`, then `7z e -so` per entry): `file entry size packed-size offset sha256`. 7-Zip expands `.rsrc` into a resource tree; those rows are left out, since this view keeps the raw section. 7-Zip does not list the trailing overlay. |

`tiny.c`, `tiny.rc` and `addcert.py` are kept beside the binaries as documentation; they are not build inputs.
