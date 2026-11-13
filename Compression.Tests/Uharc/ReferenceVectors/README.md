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
| `basic-mx.uha` | basic | `-mx` | PPM, a text stream switching to binary coding after 15 bytes |
| `reset-mx.uha` / `reset-mx1.uha` | reset | `-mx` / `-mx -md1` | PPM reset points; a 1 KiB window wrapping hundreds of times |
| `exe-mx.uha` | exe | `-mx` | PPM binary stream and the executable filter's operand byte swap |
| `mm-mx.uha` | mm | `-mx` | PPM over the multimedia set |
| `basic-mr.uha` | basic | `-mr` | RLE |
| `basic-mw.uha` | basic | `-mw` | LZ78 (32768 phrases) |
| `runs-mr.uha` / `runs-mw.uha` | runs | `-mr` / `-mw` | RLE marker rotation, 16-bit runs; LZ78 run code |
| `reset-mw1.uha` | reset | `-mw -md1` | LZ78 with 1024 phrases, pruned many times |
| `exe-mw.uha` | exe | `-mw` | the executable filter behind LZ78 |
| `reset-m2.uha` / `reset-mz.uha` | reset | `-m2` / `-mz` | 252 KiB over 18 files: model resets at file boundaries |
| `mm-m2.uha` | mm | `-m2` | ALZ multimedia selectors 1, 2, 3, 4, 5, 8, 9 |
| `mm-pair-m2.uha` | mm | `-m2`, files `u8s.wav s8n.raw` | ALZ multimedia selectors 6 and 7 |
| `mm-mz.uha` | mm | `-mz`, files `pic.bmp u8.wav s16.wav` | LZP's BMP/WAV delta filter |
| `exe-m2.uha` / `exe-mz.uha` | exe | `-m2` / `-mz` | E8/E9 + MZ relocation filter, ELF, and the 1 KiB threshold |

The `v02-*` archives were written by **UHARC 0.2** (`UHARC.EXE`, DOS, 21 Dec 1997, from
<https://archive.org/details/UHARC>, MD5 `6e2f0e8d0e6b83c0c05c9b406ce056fb`) in DOSBox, from the
same inputs, with `uharc a -y -r <switches> <archive>`; DOSBox gave the inputs the local time
2005-10-01 12:00, which is what 0.2 stored:

| Archive | Set | Switches | What it pins |
| --- | --- | --- | --- |
| `v02-basic-m2.uha` | basic | `-m2` | 0.2 container, directories, LZW tables, main coder |
| `v02-exe-m0.uha` / `v02-exe-m2.uha` | exe | `-m0` / `-m2` | stored executables stay unfiltered; the 0.2 E8/E9 filter |
| `v02-reset-m3.uha` / `v02-reset-md.uha` | reset | `-m3` / `-m3 -md-` | 256 KiB and 1 KiB histories over 18 files |
| `v02-mm-m3.uha` | mm | `-m3` | the multimedia literal modes |
| `v02-runs-m2.uha` | runs | `-m2` | byte runs |

The `v04-*` archives were written by **UHARC 0.4 BETA** (`UHARC04.EXE`, Win32, 28 Dec 2001, MD5
`72fbcde29093186d9027a8ba2feac7f1`) from the same inputs, with
`uharc04 a -y -d0 -r -ed <switches> <archive> <files>`; it stored the inputs' times as
`2005-10-01 11:00:00`. The `dates` set carries one time per file (see `genfx.py`):

| Archive | Set | Switches | What it pins |
| --- | --- | --- | --- |
| `v04-basic-m0.uha` | basic | `-m0` | 0.4 container, directories, empty file, LZP name table, LZW date/size tables |
| `v04-basic-mz.uha` / `v04-basic-mz-pw.uha` | basic | `-mz` / `-mz -pwsecret` | 0.4 LZP; enciphered tables and stream, password `secret` |
| `v04-reset-mz.uha` / `v04-reset-mz1.uha` | reset | `-mz` / `-mz -md1` | 0.4 LZP model resets; a 1 KiB window |
| `v04-exe-mz.uha` | exe | `-mz` | the executable filter behind 0.4 LZP |
| `v04-mm-mz.uha` | mm | `-mz`, files `pic.bmp u8.wav s16.wav` | the multimedia delta filter behind 0.4 LZP |
| `v04-runs-mz.uha` | runs | `-mz` | 0.4 LZP runs and 256-byte copies |
| `v04-dates-mz.uha` | dates | `-mz` | per-file dates and times, two directory levels |

`UHARC04.EXE t` reports "All files OK" for every `v04-*` archive.

Every archive here is also tested by the unpacker of unuharc_recon
(<https://github.com/roytam1/unuharc_recon>, revision
`37f1953ac57609b95ab738bb45991d6d00415789`, built from source and run as a black box), which
reports "All files OK" for each of them.

Which selectors a stream uses is the encoder's choice, recorded here so a regression in one
predictor shows up as a failing fixture rather than a silent gap.

`manifest.tsv` lists every input file: set, path, size and SHA-256 (`-` for an empty
directory). UNUHARC 0.6b (`UNUHARC.EXE t`) reports "All files OK" for every archive.

UHARC is freeware for non-commercial use; these files are its output from our inputs, not part
of UHARC itself.
