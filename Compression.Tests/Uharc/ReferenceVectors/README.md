# UHARC reference vectors

Every `.uha` file here was written by **UHARC 0.6b** (`UHARC.EXE`, Win32, 01 Oct 2005, from
`uharc06b.zip` as archived at <https://archive.org/details/uharc06b>, MD5 of the zip
`1ebeb57946cfd7e998e5a7be78f2b0bc`) from inputs that `genfx.py` generates — our own bytes, no
third-party content. The inputs' modification times were set to 2005-10-01 12:00 local time
(CET); UHARC stored them as `2005-10-01 11:00:00`, which is what the tests expect.

Command, run inside the input set's directory:

    uharc a -y -d0 -r -ed <switches> <archive> <files>

| Archive | Set | Switches | What it pins |
| --- | --- | --- | --- |
| `basic-m0.uha` | basic | `-m0` | store, empty file, empty directory, sub-directory |
| `basic-m1.uha` / `-m2` / `-m3` | basic | `-m1` / `-m2` / `-m3` | ALZ:1..3 |
| `basic-mz.uha` | basic | `-mz` | LZP |
| `basic-m2-pw.uha` | basic | `-m2 -pwsecret` | enciphered directory and stream, password `secret` |
| `basic-mx.uha` | basic | `-mx` | PPM: lists, refuses to extract |
| `basic-mr.uha` | basic | `-mr` | RLE: lists, refuses to extract |
| `basic-mw.uha` | basic | `-mw` | LZ78: lists, refuses to extract |
| `reset-m2.uha` / `reset-mz.uha` | reset | `-m2` / `-mz` | 252 KiB over 18 files: model resets at file boundaries |
| `mm-m2.uha` | mm | `-m2` | ALZ multimedia selectors 1, 2, 3, 4, 5, 8, 9 |
| `mm-pair-m2.uha` | mm | `-m2`, files `u8s.wav s8n.raw` | ALZ multimedia selectors 6 and 7 |
| `mm-mz.uha` | mm | `-mz`, files `pic.bmp u8.wav s16.wav` | LZP's BMP/WAV delta filter |
| `exe-m2.uha` / `exe-mz.uha` | exe | `-m2` / `-mz` | E8/E9 + MZ relocation filter, ELF, and the 1 KiB threshold |

Which selectors a stream uses was read off an instrumented build of an independent decoder; it
is a property of UHARC's encoder, recorded here so a regression in one predictor shows up as a
failing fixture rather than a silent gap.

`manifest.tsv` lists every input file: set, path, size and SHA-256 (`-` for an empty
directory). UNUHARC 0.6b (`UNUHARC.EXE t`) reports "All files OK" for every archive.

UHARC is freeware for non-commercial use; these files are its output from our inputs, not part
of UHARC itself.
