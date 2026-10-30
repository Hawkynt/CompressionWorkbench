# LiveMaker VF reference vectors

| File | Origin |
| --- | --- |
| `pylm-v102.dat` | written by pylivemaker 1.2.1 (`LMArchive(..., mode="w")`, PyPI, Python 3.13, Windows 11): `hello.txt` (`Hello world!\n`, stored), `sub\data.bin` (bytes 0..255 sixteen times, zlib) and `日本語.txt` (`こんにちは ` twenty times in CP932, zlib). pylivemaker appends its `offset + "lv"` trailer. |
| `pylm-scrambled.dat` | written by pylivemaker 1.2.1 with `writebytes`: `plain.txt` stored, `scr.bin` (method 2, LiveMaker Pro scramble, chunk 256, seed 0x1234) and `scrz.txt` (method 3, scramble over zlib, chunk 37, seed 0xBEEF). The scrambled bytes were laid out with pylivemaker's own `LMScramble.randseq`, and pylivemaker's `decrypt` and GARbro both restore the originals (script: `make-scrambled.py.txt`). |
| `cwb-v102.dat`, `cwb-v102-stored.dat` | written by `LiveMakerFormatDescriptor.Create` (default, and `method=store`) from the `Inputs()` of `LiveMakerTests.cs`. |
| `pylivemaker-listing.txt` | pylivemaker 1.2.1's `LMArchive.read(..., skip_checksum=False)` of every member of the four files above: name, size, method, SHA-256. Reading with the checksum check means pylivemaker accepted our CRC-32 values. |
| `garbro-listing.txt` | GARbro 1.5.44's `ArcFile.TryOpen` / `OpenEntry` of the same files (driver: `Compression.Tests/Xp3/ReferenceVectors/GarbroOracle.cs.txt`). |

Not committed (GPL-licensed test data): pylivemaker's `tests/data/save.dat`, a save archive written by
LiveMaker itself for its LiveNovel tutorial game. It fixed the layout of the per-member arrays (unknown
word 0, CRC-32 of the stored bytes, encryption flag 0) and that LiveMaker writes no trailer; our reader
extracts its `STATUSSAVEDATA` member to the bytes pylivemaker extracts, and `CWB_LIVEMAKER_SAVE`
repeats that check.
