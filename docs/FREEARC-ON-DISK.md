# FreeArc on disk

The container layout of FreeArc archives (`.arc`, signature `ArC\x01`) as FreeArc 0.4x–0.67 writes
them, and what `FileFormat.FreeArc` does with each part.

## 🔎 Provenance

- **Licence.** FreeArc's sources (Bulat Ziganshin, 2004–2014) are GPL-2.0; the 0.67 binaries carry
  a freeware usage licence. Neither can be absorbed into this LGPL-3.0-or-later repository, so this
  is rung 2 of the sourcing ladder: the sources were *read* to write this document, the
  implementation was written from this document, and FreeArc 0.67 is the oracle. No FreeArc code,
  file layout or identifier names were carried across.
- **Sources read.** The SVN snapshot mirrored at `github.com/svn2github/freearc`: `Unarc/ArcStructure.h`
  (the C++ reader of footer and directory), `ArhiveStructure.hs`, `ArhiveDirectory.hs`,
  `ByteStream.hs` and `Compression.hs` (the Haskell writer).
- **Oracle.** FreeArc 0.67 (2014-03-15) `Arc.exe` and `unarc.exe`, from the self-extracting
  `FreeArc-console-0.67-alpha-win32.exe` (Wayback Machine copy of freearc.org/download/testing),
  run on Windows 11. Every structure below was checked against hex dumps of archives it wrote; the
  checked-in fixtures are in `Compression.Tests/FreeArc/ReferenceVectors/`.

## 🧱 Primitives

All fixed-width integers are little-endian.

| Primitive | Encoding |
|-----------|----------|
| integer | Variable length, 1–9 bytes. The number of trailing one-bits in the first byte, *n*, says the value occupies *n*+1 bytes: read those bytes little-endian and shift right by *n*+1. So `xxxxxxx0` holds 7 bits, `…01` 14 bits, `…011` 21 bits, up to `01111111` + 7 bytes = 56 bits. A first byte `0xFF` is followed by a plain 64-bit value. Writers use the shortest form. Measured: `10` = 8, `AA` = 85, `31 A4` = 10508. |
| string | UTF-8 bytes, NUL-terminated. |
| list of *T* | An integer count, then the elements — except where a count is implied (see the directory block). |
| bool | One byte, 0 or 1. Lists of bools are *not* bit-packed. |
| CRC | CRC-32 (the zlib/PKZIP polynomial `0xEDB88320`, initial and final XOR `0xFFFFFFFF`), 4 bytes. `"Hello, FreeArc!\n"` → `2B27C831`. |
| time | 32-bit Unix seconds, UTC. |

## 🗂️ Archive structure

An archive is a sequence of blocks. Data blocks hold file contents; every other block is a
*control block* and is immediately followed by its *descriptor*:

```
signature  "ArC\x01"                     4 bytes
type       integer                       0 descriptor, 1 header, 2 data, 3 directory, 4 footer, 5 recovery
compressor string                        method that encoded the block
origsize   integer                       decoded size
compsize   integer                       encoded size; the block starts compsize bytes before the descriptor
crc        CRC of the decoded block
dcrc       CRC of all descriptor bytes before it
```

FreeArc 0.67 lays an archive out as:

| Offset | Content |
|--------|---------|
| 0 | Header block: `ArC\x01` + version word `00 00 06 07` (8 bytes, `storing`), then its descriptor. Anything before the header block is an SFX module. |
| … | Data blocks (solid blocks), no descriptors. |
| … | Directory block (compressed with `-dm`, default `lzma:1mb:normal:bt4:32`), then its descriptor. |
| … | Footer block (same method), then its descriptor, which runs to the end of the file. |

A reader starts at the end: the footer's descriptor begins within the last 4096 bytes. FreeArc takes
the last `ArC\x01` in that window; our reader scans backwards and takes the first candidate whose
descriptor CRC checks out and whose type is footer, which tolerates the signature appearing inside
compressed data.

### Footer block

```
blocks     list of (type integer, compressor string, relpos integer, origsize integer, compsize integer, crc CRC)
locked     bool                           archive locked against modification
oldcomment list of 32-bit code points     pre-UTF-8 comment, written empty by 0.67
recovery   string                         recovery-record setting (optional, absent in old archives)
comment    list of bytes                  UTF-8 comment (optional)
```

`relpos` is the distance from the start of *this footer block* back to the listed block. FreeArc
0.67 lists the header block and every directory block (and the recovery block when there is one).
The trailing fields are optional: a reader stops at the end of the block.

### Directory block

The positions of data blocks are relative to the start of *this directory block*. The per-block and
per-file lists carry no counts of their own; their lengths are the block count and the file total.

```
nblocks        integer
files[nblocks] integer each                 files in each solid block
methods[nblocks] string each                compressor of each solid block
relpos[nblocks]  integer each               distance back from the directory block
compsize[nblocks] integer each
ndirs          integer
dirs[ndirs]    string each                  directory names, "/"-separated; "" for the root
-- nfiles = sum(files) --
names[nfiles]  string each                  base names
dirnum[nfiles] integer each                 index into dirs
size[nfiles]   integer each
time[nfiles]   time each
isdir[nfiles]  bool each
crc[nfiles]    CRC each                     0 for directories
tag            integer                      optional tagged fields would follow; 0 ends them (0.67 writes only 0)
```

Files belong to solid blocks in order: the first `files[0]` files are the contents of block 0
concatenated, and so on. A data block's decoded size is therefore the sum of its files' sizes; it is
not stored anywhere. FreeArc puts directory entries (size 0) into a zero-length `storing` block of
their own. The full name of a file is `dirs[dirnum] + "/" + name` (or just `name` for the root).
A directory entry `a/b` is stored as name `b` in directory `a`.

## 🧪 Methods

A compressor string is one or more stages joined by `+`, each `name:param:param…`, applied left to
right on compression; extraction undoes them right to left. Seen from 0.67:

| Method string | Meaning | Here |
|---------------|---------|------|
| `storing` | Uncompressed. | ✅ read + write |
| `lzma:32kb:normal:32[:pb1:lc4:lp1][:mc4]` | Raw LZMA1 range-coded stream, **no properties header**; the dictionary is the token with a size suffix, `lcN`/`lpN`/`pbN` override the defaults 3/0/2, everything else steers only the encoder. FreeArc shrinks the dictionary to the data size. | ✅ read + write (we write an end marker) |
| `ppmd:10:48mb` | PPMd var. I (order, memory). Our PPMd I model is not verified against FreeArc's stream. | ❌ refused |
| `tor:…`, `4x4:…`, `rep:…`, `lzp:…`, `grzip:…`, `dict:…`, `delta`, `exe`, `mm`, external compressors | FreeArc's own codecs and filters, no compatible implementation here. | ❌ refused |
| `…+aes-256/ctr:n1000:r0:i…:s…:c…` (also blowfish, serpent, twofish) | Encryption stage (`-p`, or `-hp` for headers too). | ❌ refused |

A refused method still lists: only extraction of that block fails, with `NotSupportedException`. An
archive whose directory or footer is unreadable (`-hp`, or an unsupported `-dm` method) lists as
`FULL.arc` + `metadata.ini` with `parse_status=partial`.

## ✍️ What our writer emits

Header block, a zero-length `storing` block for directory entries (when there are any), one solid
block for all files (`storing` or `lzma:<dict>[:lcN:lpN:pbN]`), then a `storing` directory block
and a `storing` footer listing the header and directory blocks. For `storing` input in FreeArc's
order with FreeArc's timestamps this is byte-identical to `arc a -m0 -dmstoring` (pinned by
`Writer_Storing_ReproducesFreeArcByteForByte`). FreeArc 0.67's `unarc t/x` and `arc t/lb/x` accept
our `storing` and `lzma` archives and extract them byte-identically (`FreeArcOracleTests`).
