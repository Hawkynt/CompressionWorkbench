# C64/C128 SFX reference vectors

`Draughts.sfx` and `Agricola.sfx` are real Commodore self-extracting LHA archives, made with
Chris Smeets' SFX extractor (3721-byte stub, banner "C64/C128 SELF EXTRACTING LHARCHIVE").
They come unmodified from <http://www.zimmers.net/anonftp/pub/cbm/c64/games/> and were downloaded
on 2026-10-30.

The oracle is unar/lsar 1.10.1 (XADMaster) on Ubuntu, which identifies both as "Self-extracting
Commodore 64 LhA". `unar-listing.txt` records its listing (`lsar -l`), its CRC test (`lsar -t`, all
OK) and the SHA-256 of every file it extracted. `C64SfxTests` checks that our listing and our
extracted bytes match it. The CBM file type that C64 LHA stores after a NUL in the name (P, S) is
reported as part of the method.

7-Zip 26.03 lists these archives but cannot decompress `-lh1-`.
