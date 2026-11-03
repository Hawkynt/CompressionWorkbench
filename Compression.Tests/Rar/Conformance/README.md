# RAR conformance fixtures

Archives written by RAR itself, from RAR 1.40 (1994) to RAR 7.12 (2025), plus libarchive's
RAR test archives. `RarConformanceTests` reads every file in every archive and compares it
byte for byte with the expected SHA-256, and it must refuse what unrar refuses. All inputs are
our own deterministic data (`tools/mkinputs.py`, `make_inputs.py` in `../ReferenceVectors`),
so the archives may be redistributed. No RAR program is committed.

Manifests are tab-separated: `archive  entry  kind  size  sha256  password`. Volume sets are
listed under their first volume.

## `Modern/`: RAR 7.12 and RAR 6.24 (WinRAR for Windows, trial)

| Tool | Source | SHA-256 of the installer |
| --- | --- | --- |
| RAR 7.12 x64 (`Rar.exe`, `UnRAR.exe`) | `https://www.rarlab.com/rar/winrar-x64-712.exe` | `80ab68244a0d86600ab250e8dba551b87f7f8b4603e2300b3bcbd7e870e90f91` |
| RAR 6.24 x64 (the last to write RAR 4 with `-ma4`) | `https://www.rarlab.com/rar/winrar-x64-624.exe` | `794481dbbc9009a2565726fb5b4a4ab2fe216ff9edbb08951548ee765de9b4a6` |

`r5-*` were written by RAR 7.12 (`-ma5`), `r4-*` by RAR 6.24 (`-ma4`), on 2026-10-30, by
`tools/genfx.py`; `commands.txt` lists every command line. UnRAR 7.12 tests every archive OK
before it is recorded. They cover methods `-m0`..`-m5`, solid and solid groups (`-s`, `-s2`,
`-se`), dictionaries (`-md64k`..`-md4g`), `-mt1`/`-mt16`, the x86/delta/audio filters, PPMd,
high-precision times (`-tsm4 -tsc4 -tsa4`), hard links (`-oh`), identical-file references
(`-oi`), NTFS streams (`-os`), comments, recovery records (`-rr5%`), locked archives (`-k`),
quick-open data (`-qo+`), BLAKE2sp (`-htb`), passwords (`-p`, `-hp`, a Unicode password),
file versions (`-ver`), and volume sets in both naming schemes, solid, encrypted, with
encrypted headers and with a recovery volume (`.rev`). `r4-prefixed.exe` and
`r5-prefixed.exe` put a 4 KB non-RAR stub in front of an archive (SFX search); real
`rar -sfx` archives are made at test time by the tool-gated test.

## `Dos/`: DOS RAR 1.40 to 3.93 under DOSBox-X

| Version | Source (shareware) | SHA-256 |
| --- | --- | --- |
| RAR 1.40.2 beta (`RE~^` format) | `http://cd.textfiles.com/nfbfiles/compress/RAR1_402.EXE` | `9a2957f9c366aae0abc43f9483bce82b7bfb74f4a5582b854f679f1ffcd22cd3` |
| RAR 1.55 | `http://cd.textfiles.com/simtel/simtel9510/disk2/DISC2/ARCHIVER/RAR155.EXE` | `ec48bfd97a609379a91069f6a2093fe4016875bb451499c53bd244e65d4102cc` |
| RAR 2.00 | `http://cd.textfiles.com/simtel/simtel9703/disk2/DISC2/ARCERS/RAR200.EXE` | `0ad50652b720912a9708f8fb2c5d8233750848c5acffc82064698d3b08b4cb32` |
| RAR 2.50 | `https://www.sac.sk/download/pack/rar250.exe` | `3862e775d7b503463608962a96fe7b21b78b643c94f6b98341a2cbc5abad24d3` |
| RAR 3.93 (DOS32, `rar32.exe`) | `https://www.sac.sk/download/pack/rarx393.exe` | `94f610c7fb9e8dc209da057d785f7affee6cdeac057b8e1b074dafcf1a10f014` |

Emulator: DOSBox-X 2026.10.01 (`dosbox-x-vsbuild-win64-2026.10.01-portable.zip`, SHA-256
`3c50915e63aefe35777d65ebb4391a06b4a2c2342e4c75cbee6bbda4c6916e33`), run headless
(`-silent -fastlaunch`, commands in a batch file). `tools/gendos.py` made every archive;
`commands-dos.txt` lists the commands. Per version: `-m0`, `-m3`, `-m5`, solid, a
subdirectory, `-p` (compressed and stored), volumes (`.rar` + `.r00`), SFX (1.55-2.50),
multimedia compression `-mm` (2.00, 2.50), and for 3.93 `-hp`, PPMd and the audio filter.
RAR 3.93 names volumes `NAME.partN.rar` by default, which an 8.3 DOS drive cannot hold, and
then writes nothing; its volumes are made with `-vn` (`.rar` + `.r00`).

## `Libarchive/`: libarchive's RAR test suite

`test_read_format_rar*.rar(.uu)` from libarchive v3.8.10 (`libarchive/test/`, BSD-2-Clause,
Copyright (c) 2003-2018 Tim Kientzle and the libarchive contributors), uudecoded, every file
under 100 KB except those already in `../ReferenceVectors`. Expected output is what UnRAR 7.12
extracts (`tools/mkla.py`): `manifest.tsv` for archives unrar extracts cleanly, `refused.tsv`
for archives it refuses at least in part (we must not produce anything it did not), and
`known-differences.tsv` for the three cases where this reader deliberately differs, with the
reason.

## Deliberate refusals

These are decisions, not gaps, and each is pinned by a test.

- **`test_read_format_rar5_bad_tables.rar`, `test_read_format_rar5_loop_bug.rar`.** Both are
  damaged and **store no checksum**. In the first, a Huffman table is over-subscribed. In the
  second, the stream ends 188 KB short of the declared size. unrar 7.12 writes something anyway
  (64 bytes for the first, and it pads the second). libarchive 3.8.10 refuses both, and so does
  this reader. With no CRC or BLAKE2 value, nothing can show that unrar's bytes are the file's
  bytes, so producing them would report a success no third party can confirm. Listing still works;
  extraction ends with `InvalidDataException`. Should a checksummed sample of either kind turn up,
  the checksum decides.
- **RAR 7 dictionaries over 4 GB (compression algorithm version 1, "v70").** Refused with
  `NotSupportedException`. Measured with rar 7.12: `-md8g` and `-mdx8g` on a 600 KB file both
  record `RAR 5.0(v50) -md=1m`. rar shrinks the recorded dictionary to the data, so a real v70
  stream needs more than 4 GB of input compressed with a dictionary over 4 GB. rar's compressor
  holds a dictionary of that size in memory. A decoder needs a window of
  min(dictionary, file size), here over 4 GB, beyond a single .NET array and beyond what the
  shared test machine (32 GB, about 14 GB free) can give both sides. No such archive could be
  made, so no decoder for version 1 can be proven, and none is claimed. Version-1 flags over a
  version-0 stream (bit 20) are read as version 0, as rar does.
- **Hidden and system files in folder inputs** (`cwb create`/`add`, `ArchiveInput.Resolve`) are
  skipped by default. That is .NET's own `EnumerationOptions` default, and on Unix it covers
  dot-files. It keeps `Thumbs.db`, `desktop.ini` and `.git` out of archives built from a folder,
  which is what a user archiving a folder expects. A file named explicitly is always taken, and
  `--include-hidden` takes everything. rar takes hidden files by default; matching it would
  quietly change what every other format's `create` collects.

## Tool-gated tests

`RarToolInteropTests` and `RarDosInteropTests` run the real programs when they are present:
`CWB_TOOL_RAR` (rar 5+), `CWB_TOOL_RAR4` (rar 6.x, for `-ma4`), `CWB_TOOL_UNRAR`,
`CWB_TOOL_7Z` (else the usual discovery), and for DOS `CWB_DOSBOX` (a `dosbox-x` or `dosbox`
executable) with `CWB_TOOL_RAR_140`, `_155`, `_200`, `_250`, `_393` naming each DOS
`RAR.EXE` (for 3.93 the `rar32.exe` beside its `emx`/`rsx` files).
