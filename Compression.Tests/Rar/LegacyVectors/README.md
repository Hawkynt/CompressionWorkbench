# RAR 1.5 / 2.0 reference archives

Archives written by the original RAR 1.5x and 2.0x archivers, taken from other projects' test suites.
`manifest.tsv` lists every packed file with its size and SHA-256 as 7-Zip 26.04 extracts it
(`7z x`); 7-Zip's `t` reports "Everything is Ok" for all four.

| File | Upstream | Licence | Content |
|------|----------|---------|---------|
| `sharpcompress-rar15.rar` | [SharpCompress](https://github.com/adamhathcock/sharpcompress) `tests/TestArchives/Archives/Rar15.rar` at `dedbf6ac57e98af471d04c8fcaae8f35a7b187b8` | MIT | unpack version 15, method m3: an EXE, a JPEG, a CP866 text |
| `sharpcompress-rar20.rar` | same repository, `Rar2.rar` | MIT | unpack version 20, method m5, the same three files (the text named in CP866, so the manifest matches it by extension) |
| `rarfile-rar15-comment.rar` | [rarfile](https://github.com/markokr/rarfile) `test/files/rar15-comment.rar` at `d2f7df6fc843dae356fd6b0a85971dc36fd6e757` | ISC | unpack version 15, one packed and one stored file, archive comment |
| `rarfile-rar20-unix-owner.rar` | same repository, `rar2-unix-owner.rar` | ISC | unpack version 20, Unix host |

SharpCompress: Copyright (c) 2014 Adam Hathcock, MIT licence. rarfile: Copyright (c) 2005-2024 Marko
Kreen, ISC licence.
