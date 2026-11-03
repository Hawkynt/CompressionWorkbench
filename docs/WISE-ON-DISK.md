# Wise installers on disk

Setup executables made by the Wise Installation System (Wise InstallMaster, InstallBuilder and
InstallMaker, versions 5 to 9): a small 16- or 32-bit stub program followed by an overlay that
holds the installer's own support files, the compiled WiseScript and the files it installs.
`FileFormat.WiseInstaller` reads them from this document.

## 🔎 Provenance

- **Layout.** Written down from the Wise reader of SabreTools.Serialization (Matt Nadareski, MIT;
  revision `9e39a0cf`), whose models cite REWise (GPL-3.0-or-later) for the overlay header and the
  script operations. Because of that lineage the reader was used as a specification and the
  implementation follows this document; no code was taken from either.
- **Measured on real installers.** Wise InstallMaster 7.01, 8.1, 8.12, Wise Installation System
  5.0, 6.0, 9.01 and 9.02 setup executables: where the overlay header sits, which header slots
  frame a stream in which version, the pad byte some streams carry before their CRC, and the
  record layouts below.
- **Oracle.** WiseUnpacker 3.0.0 (the SabreTools reference extractor). Every file it extracts from
  the 7.01, 8.1, 8.12, 9.01 and 9.02 installers is reproduced byte for byte under the same name.
  It does not read the 5.0 and 6.0 scripts; their files are checked by the CRC-32 each stream
  carries, against the size and (from 7.0 on) the CRC-32 the script records.

## 🧱 Streams

Everything in the overlay is a sequence of **streams**: raw DEFLATE data (RFC 1951, no zlib or
gzip wrapper), then the CRC-32 (ISO-HDLC, the zlib polynomial) of the inflated bytes as a
little-endian `u32`. Some streams have one extra byte between the end of the DEFLATE data and
the CRC. A stream's **stored size** counts all of it: DEFLATE data, pad byte if any, CRC.

Installers whose header flags have bit `0x100` set wrap each stream in a PKZIP local file header
instead; no such installer was available and they are refused.

## 🗂️ Overlay header

The header follows the stub executable: right after the last section of a 32-bit stub, after the
segments of a 16-bit stub, and — for setups that wrap the stub in a launcher — after the stub
carried as a resource of the launcher. Rather than computing that place for each stub kind, the
reader searches the first 512 KiB, at 16-byte boundaries (where every stub kind ends), for the
offset at which the header below is consistent **and** its first stream inflates to the size the
header gives, with a matching CRC.

All integers little-endian.

| Size | Field |
| ---: | --- |
| 1 | `n`, length of a DLL name (usually 0) |
| `n` + 4 | DLL name and its size, only when `n` > 0 |
| 4 | flags (`0x100`: PKZIP-wrapped streams) |
| 12 | colours of the background gradient |
| 4 | offset of the exit event in the script |
| 4 | offset of the cancel event in the script |
| 4 | script, inflated size |
| 4 | script, stored size |
| 9 × 4 | slots 1 to 9: stored sizes of support files |
| 4 | final support file, stored size |
| 4 | final support file, inflated size |
| 4 | length of the installer, or 0 |

Three endings follow, told apart by where a marker `u16` of `0x0800` (or `0x0008`) sits:

| Ending | Fields after the length |
| --- | --- |
| oldest (5.x, 6.x) | none — the streams start here |
| 7.x to 9.x | bitmap stored size `u32`, bitmap inflated size `u32`, marker `u16`, text length `u8`, text |
| later | bitmap stored and inflated sizes, install script stored size `u32`, character set `u32`, marker, text length, text |

The text is the font name and the start-up messages, NUL-separated.

**Header streams** follow in this order: the background bitmap (when the header has one), the
script, the support files of slots 1 to 9, the install script (later ending only), the final
support file. A slot holds a stream only when the stream found at the current position has
exactly the stored size the slot gives; otherwise the slot's value means something else in that
version and is skipped:

| Version | Slots that frame a stream |
| --- | --- |
| 5.0 | 1, 9 |
| 6.0 | 1, 5, 6, 7, 8, 9 |
| 7.01 | 1, 2, 5, 6, 7, 8, 9 |
| 8.1, 8.12 | 1, 4, 5, 6 |
| 9.01, 9.02 | 1, 5 |

The script and the final support file must be present, and the bitmap when the header gives
its size. The **data base** is the offset right after the final support file; every offset in
the script counts from there.

The header streams are named as the reference extractor names them:

| Stream | Bitmap | Script | Slot 1 | 2 | 3 | 4 | 5 | 6 | 7 | 8 | 9 | Install script | Final |
| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |
| Name | `WiseColors.dib` | `WiseScript.bin` | `WISE0001.DLL` | `CTL3D32.DLL` | `FILE0004` | `Ocxreg32.EXE` | `PROGRESS.DLL` | `FILE0007` | `FILE0008` | `FILE0009` | `FILE000A` | `INSTALL_SCRIPT` | `FILE00XX.DAT` |

Slot 1 is the installer's runtime DLL; the other names say which slot a stream came from rather
than what it is.

## 📜 Script records

The script is a header (installer settings, language count, message strings) followed by
operations, each an opcode byte and an operation-specific body whose layout changed between
versions. The reader does not walk the operations. It looks for the three record kinds that
point into the data, and accepts a record only when the stream it points at inflates to the
size it gives and — when it records a CRC — to that CRC:

**Install a file** — opcode `0x00`:

| Size | Field |
| ---: | --- |
| 2 | flags |
| 4 | start of the stream, from the data base |
| 4 | end of the stream (start + stored size) |
| 2 | DOS date |
| 2 | DOS time |
| 4 | inflated size |
| 20 | unused here |
| 4 | CRC-32 of the inflated file, 0 before 7.0 |
| … | destination path, NUL-terminated, Windows-1252 |
| … | one description per language and a source path, NUL-terminated |

The destination is a Windows path that starts with a script variable: `%MAINDIR%\Help\x.pdf`,
`%TEMP%\%EULA%`. The same stream can be installed under several names, and conditional records
can install different streams under one name.

**Custom dialog set** — opcode `0x14`: start, end, inflated size (`u32` each), then the display
variable and the dialog name, NUL-terminated.

**Billboard** — opcode `0x06`: flags `u16`, two `u16` operands, then one start, end and inflated
size triple per language, then a terminating 0 byte.

Streams no record points at — wizard graphics and other data of operations the reader does not
interpret — are still streams; the reader lists them by offset.

## 🏷️ Names

| Data | Name |
| --- | --- |
| Header stream | the name in the table above |
| Installed file | the destination with every `%` removed and `\` turned into `/`: `MAINDIR/Help/x.pdf`, `TEMP/EULA` |
| Two records, one name, different streams | the later record keeps the name (as the installer would leave it); earlier ones get `~1`, `~2`, … appended |
| Custom dialog set | `CustomDialogSet_<variable>-<name>` (`%` removed, characters that cannot appear in a file name replaced by `_`) |
| Billboard | `CustomBillboardSet_<flags as 4 hex digits>-<operand 2>-<operand 3><language index as 4 hex digits>` |
| Any other stream | `DATA/<offset from the data base as 8 hex digits>.bin` |

Files the reference extractor writes that are not stored in the installer — INI files it
assembles from *Edit INI file* operations, and empty directories — are not produced.

## 🚧 Not read

- Multi-volume installers (`.W02`, `.002`, ...): records pointing past the end of the file are
  not listed, and `metadata.ini` counts them.
- PKZIP-wrapped streams (flag `0x100`).
- Wise for Windows Installer (an MSI authoring tool) and newer Wise setups that keep their data
  in a `.WISE` PE section.
