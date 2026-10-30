# RAR 1.5 / 2.0 reference archives

Archives written by the original RAR 1.5x and 2.0x archivers, taken from other projects' test suites.
`manifest.tsv` lists every packed file with its size and SHA-256 as 7-Zip 26.04 extracts it
(`7z x`); 7-Zip's `t` reports "Everything is Ok" for all of them.

| File | Upstream | Licence | Content |
|------|----------|---------|---------|
| `sharpcompress-rar15.rar` | [SharpCompress](https://github.com/adamhathcock/sharpcompress) `tests/TestArchives/Archives/Rar15.rar` at `dedbf6ac57e98af471d04c8fcaae8f35a7b187b8` | MIT | unpack version 15, method m3: an EXE, a JPEG, a CP866 text |
| `sharpcompress-rar20.rar` | same repository, `Rar2.rar` | MIT | unpack version 20, method m5, the same three files (the text named in CP866, so the manifest matches it by extension) |
| `rarfile-rar15-comment.rar` | [rarfile](https://github.com/markokr/rarfile) `test/files/rar15-comment.rar` at `d2f7df6fc843dae356fd6b0a85971dc36fd6e757` | ISC | unpack version 15, one packed and one stored file, archive comment |
| `rarfile-rar20-unix-owner.rar` | same repository, `rar2-unix-owner.rar` | ISC | unpack version 20, Unix host |

SharpCompress: Copyright (c) 2014 Adam Hathcock, MIT licence. rarfile: Copyright (c) 2005-2024 Marko
Kreen, ISC licence.

## WinRAR 2.90 vectors

`winrar290-*.rar` were written on 2026-10-30 by the console `Rar.exe` of WinRAR 2.90
(rarlab.com `wrar290.exe`, sha256 `7c2e3ec5c5eccc01d38374f6ef317ae1e22ac84eac159a7ce37a4f9e1d9579f6`,
evaluation copy, unpacked with 7-Zip). RAR 2.90 still writes unpack version 20. The inputs come
from `make_winrar290_inputs.py` (deterministic, fixed seed); the manifest lists the inputs' hashes,
which is also what 7-Zip 26.04 extracts.

| File | Command | What it covers |
|------|---------|----------------|
| `winrar290-audio.rar` | `rar a -ep -mmf -m3` mono8.wav stereo16.wav quad8.wav | multimedia (audio) blocks: 8-bit mono, 16-bit stereo, 8-bit four channels |
| `winrar290-solid.rar` | `rar a -ep -s -m3` text.txt table.bin mixed.bin | a solid stream whose tables travel once |
| `winrar290-solid-mixed.rar` | `rar a -ep -s -mm -m5 -md64` text.txt table.bin mono8.wav stereo16.wav | a solid stream switching between LZ and audio tables, 64 KB window |
| `winrar290-best-1m.rar` | `rar a -ep -m5 -md1024` text.txt mixed.bin | 1 MB window |
| `winrar290-fastest.rar` | `rar a -ep -m1` text.txt table.bin | fastest method |
