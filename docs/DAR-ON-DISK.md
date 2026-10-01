# DAR — on-disk notes

DAR is the archive format of `dar` and libdar (Denis Corbin). There is no format standard.
dar's own documentation describes the structure in prose (`doc/Notes.html`, "Archive structure in
brief"), but gives no byte layouts. dar and libdar are GPL, which this LGPL repository cannot take
code from, so this is rung 2 of the sourcing ladder in `AGENTS.md`. The libdar 2.8.6 sources were
read only to work out *what the bytes are*. That knowledge is written down here, and
`FileFormat.Dar` is implemented from this document. No libdar code, file layout or identifier
was carried across.

Every layout below was then checked against archives the real tool wrote, and against what the
real tool makes of archives we write:

| Oracle | Where |
| --- | --- |
| dar 2.7.13 (libdar 6.7.1, Ubuntu 24.04 package) | WSL; `Compression.Tests/Dar/DarExternalConformanceTests.cs` runs it both ways |
| dar 2.8.6 (`dar64-2.8.6-win64`) | the `single` and `sliced` vectors under `Compression.Tests/Dar/ReferenceVectors` |

Both versions write archive format **11.3**.

Labels used below: **[doc]** means dar's documentation says it. **[src]** means it was read from
libdar's source. **[measured]** means it was confirmed byte for byte in dar output, or dar
accepted bytes we wrote that depend on it.

## 1. Layers

From outside in [doc]: **slices** split the archive into files. Inside them is an optional
**encryption** layer (refused here), an optional **escape** layer (tape marks), a **compression**
layer, and an optional **sparse** layer per file. Glue the slice payloads together and you get the
*archive level*:

    version header | file data … | catalogue | terminator 1 | version trailer | terminator 2

In direct-access mode, which is dar's default and the only mode this reader uses, it works
backwards from the end. Terminator 2 gives the version trailer, which describes the layers.
Terminator 1, the bytes just before the trailer, gives the catalogue. The catalogue gives every
file's offset [doc][measured].

## 2. Primitive encodings

### 2.1 infinint

An unsigned integer of any width [src][measured]. Write *N* zero bytes, then one byte with exactly
one bit set. Bit 7 means one 4-byte group follows, bit 6 means two, and so on down to bit 0 for
eight. Each zero byte adds eight more groups. The value follows big-endian, left-padded to a whole
number of groups. dar always picks the smallest group count, and zero is one group:
`80 00 00 00 00`. Examples from dar output: `80 00 00 00 0A` is 10, and
`40 00 00 00 01 40 00 00 00` is 5 GiB.

### 2.2 Strings

Strings are UTF-8 bytes followed by NUL [src][measured].

### 2.3 Checksum ("CRC")

This is not a CRC. Every byte is XOR-ed into a ring of *w* bytes, starting at slot 0, and wraps
at the end [src][measured]. It is stored as an infinint *w*, then the *w* bytes. The widths are:

| Protected thing | Width |
| --- | --- |
| version header / trailer | 2 |
| catalogue | 4 |
| file data | 1 when empty, otherwise 4 per started GiB |
| inline entry copies (tape marks) | 2 |

A file's checksum covers its **original** data: after decompression and after holes are expanded
[measured]. It is not taken over the stored bytes. `hello dar\n` gives `75 4F 08 0D`.

### 2.4 Dates

One unit byte, then an infinint count of seconds since the Unix epoch, then (for `u` and `n` only)
an infinint fraction [src][measured]. The units are `s` (seconds), `u` (microseconds) and `n`
(nanoseconds). Formats before 9 have no unit byte [src]. dar writes `n` for real timestamps and
`s 0` for ones it does not know.

### 2.5 Flag field

The flag field is big-endian bytes, most significant first [src][measured]. Bit 0 of each byte
means another byte follows, so it is not a flag. A reader drops bit 0 and shifts each byte in.
A writer that needs bits above 7 sets bit 0 in every byte except the last. So flag 0x0800 is
written `09 00`.

## 3. Slices

[doc] for the overall shape, [measured] for the values (the reader's `DarSliceHeader`):

    magic u32 BE = 123 | internal name (10 bytes) | flag | extension | TLV list … | payload … | trailer (1 byte)

- **flag**: `T` means last slice, `N` means not last, and `E` means the trailer byte decides. dar
  writes `T` for a single slice and `E` on every slice of a multi-slice set.
- **extension**: format 8 and later always write `T`, a TLV list. The list is an infinint count,
  then for each entry a u16 BE type, an infinint length and the value. Type 1 is the slice size,
  type 2 the first slice size (both infinints), and type 3 the 10-byte **data name**. A single
  slice carries only type 3.
- **trailer**: `T` or `N`. It is the last byte of every slice, including a single slice.
- The internal name is shared by every slice of one archive. For a normal archive the data name
  is the same 10 bytes [doc]. dar fills them with the clock and process id [src]. Any value
  unique to the archive works [measured].
- Slices are named `basename.N.dar`, with N counting from 1.
- The archive-level stream is the payloads glued in slice order: each slice from the end of its
  TLV list to just before its trailer [measured]. The slice-size TLVs count the whole slice file,
  header and trailer included.

## 4. Version header and version trailer

They share one layout. The header is written at offset 0 of the archive level, and the trailer
before terminator 2 [src][measured]:

    edition (4) | compression (1) | user comment (string) | flags | [fields the flags announce] | checksum (w = 2)

- **edition**: `char(major / 256 + 48)`, `char(major % 256 + 48)`, `char(minor + 48)`, NUL. So
  11.3 is `30 3B 33 00` ("0;3"). A naive `"113\0"` decodes as major 257, and dar rejects it with
  "format version of the archive is too high". That was the earlier writer's bug [measured].
- **compression**: `n` none, `z` gzip, `y` bzip2, `x` xz, `d` zstd, `q` lz4, and `l`/`j`/`k` lzo.
  Uppercase letters exist for per-block mode, but the header carries the lowercase letter
  [src][measured].
- **user comment**: dar writes `N/A` [measured].
- **flags** [src]:

  | Bit | Meaning | Field it adds, in this order |
  | --- | --- | --- |
  | 0x20 | encrypted | crypto algorithm (1 byte) |
  | 0x10 | tape marks (escape layer) present | — |
  | 0x08 | initial offset recorded | infinint: where file data starts |
  | 0x04 | asymmetric-encrypted key present | infinint size + key |
  | 0x02 | reference slicing (isolated catalogue) | 4 infinints + 1 byte |
  | 0x0200 | signed | — |
  | 0x0400 | KDF parameters | salt size, salt, iteration count, hash byte |
  | 0x0800 | block compression | infinint block size |

  dar's default archive has `10` in the header and `18` in the trailer (initial offset 17, just
  past the 17-byte header). With `-at` both drop 0x10. With a `-z…:…:16k` block size, `0x0800`
  appears and the trailer flags are written `09 18` [measured].

The checksum covers every byte before it [measured: `…4E 2F 41 00 00` → `42 34`].

## 5. Terminators

A terminator records one position and is read **backwards** from where it ends [src][measured].
Forwards it is the infinint, zero-padded to whole 4-byte blocks, then a byte whose top *k* bits are
set (*k* = number of blocks mod 8, or a zero byte if that is 0), then one `FF` per further eight
blocks. Reading backwards: count the `FF`s (×8), add the set top bits of the next byte, multiply by
4, and step back that many bytes to the infinint. Position 30 is
`80 00 00 00 1E 00 00 00 C0` [measured]. Both positions are offsets into the archive level.

## 6. Escape layer (tape marks)

This layer lets dar read an archive sequentially, for example from a pipe [doc]. A **mark** is
`AD FD EA 77 21` followed by one type byte [src][measured]: `D` data name, `P` in-place path, `F`
inode, `R` file data checksum, `C` catalogue, `E`/`r` EA and its checksum, `S`/`s` FSA and its
checksum, `d` delta signature, `W` changed-data copy, `I` dirty, `!` failed backup.

If data that passes through the layer contains `AD FD EA 77 21`, the writer emits those five bytes
plus `X`, and the reader drops the `X` [src][measured: the fixture `escape.bin`]. Everything at
the archive level except the terminators and the version trailer passes through it, including
the catalogue.

Positions in the catalogue are offsets in the escaped stream. *Stored sizes* count the logical
(unescaped) bytes, and a real mark ends the data [src][measured]. So the reader seeks to the
offset, removes escapes and stops after the stored size.

With tape marks, every saved inode is also written inline as a mark `F`, a short copy of its
entry, and a 2-byte checksum. Its data follows, then `R` and the data checksum. The direct-access
reader never needs these copies.

## 7. Compression layer

Each file's data is compressed **on its own**: the compressor is finished and reset between files
[src][measured]. The catalogue is compressed as one more such unit. Terminators and the version
trailer are never compressed. dar stores a file uncompressed (letter `n` in its entry) when it is
shorter than the `-m` minimum, matches `-Z`, or would not shrink [measured].

- **Streaming mode** (no block size in the header): gzip is a zlib stream (`78 DA …`), bzip2 a
  `.bz2` stream, xz an `.xz` stream and zstd a zstd frame [src][measured]. lz4 and lzo still use
  the block framing below, with 246 660 clear bytes per block [src].
- **Block mode** (flag 0x0800): every algorithm uses the framing [src][measured]. Each block is a
  type byte `01`, an infinint compressed size and the block. Each block compresses at most
  *block size* clear bytes on its own, as a zlib stream, `.bz2`, `.xz`, zstd frame, raw LZ4 block
  or LZO1X block. A type byte `02` with size 0 ends the unit. Uncompressed (`n`) files are raw in
  both modes.

## 8. Sparse files

When a file's data flags have bit 0x01 set, its clear data (after decompression) carries **hole
records** [src][measured]:

- `AE FD EA 77 21 46` followed by an infinint *n* stands for *n* zero bytes.
- `AE FD EA 77 21 58` stands for the five prefix bytes as data.

Holes are found by content, so dar uses them on any file system. 70 000 zeros become a 6-byte
mark plus a 5-byte infinint [measured: the fixture `sparse.bin`].

## 9. Catalogue

[doc] for the shape, [src] for the fields, [measured] on every fixture:

    data name (10) | in-place path (string, format ≥ 11.1) | root directory entry … | checksum (w = 4)

The in-place path is the `-R` root and must be absolute or `.` [src]. The checksum covers
everything before it, uncompressed and unescaped.

Every entry starts with a **signature byte**. The low five bits are a type letter (OR with 0x60
recovers it). The top three bits are the saved status: 1 delta patch, 2 not saved (unchanged
since the archive of reference), 3 saved, 4 inode metadata only, 7 placeholder. 0, 5 and 6 are
invalid. A directory lists its children and ends with an end-of-directory entry, so paths are
rebuilt from nesting [doc]. The root is a full `d` entry named `root`, with uid, gid and
permissions 0 and its own `z`.

| Type | Body after the signature |
| --- | --- |
| `z` end of directory | — |
| `d` directory | name, inode, children…, `z` |
| `f` file, `o` door | name, inode, file part |
| `l` symlink | name, inode, target (string, only when saved) |
| `c`/`b` device | name, inode, major u16 BE, minor u16 BE (only when saved) |
| `p` pipe, `s` socket | name, inode |
| `m` hard link | name, infinint label, `>` + a full entry (name empty) the first time; `X` for later names |
| `x` removed since the reference | name, the type letter it had, a date |

`i`/`j` entries (ignored) exist in memory but are never written [src].

**Inode**: a flag byte, uid and gid (infinints), permissions (u16 BE), then the access,
modification and change dates. Flag bits 0–2 are the EA status: 1 full, 2 partial, 3 none,
4 fake, 5 removed. Bits 3–4 are the FSA status: 0x00 none, 0x08 partial, 0x10 full.
Then come, in order [src]:

- if EA is full: EA size, EA offset and EA checksum;
- if FSA is not none: FSA families (infinint);
- if FSA is full: FSA size, FSA offset and FSA checksum.

dar on Linux ext4 records FSA (`-L-` in `dar -l`) for every inode [measured].

**File part**: size (infinint), then:

- if saved or delta: offset, stored size, data flags (`01` holes, `02` dirty, `04` delta
  signature), the compression letter, the patch base checksum (delta with a signature, format
  ≥ 11.2 only), then the data checksum;
- otherwise (format ≥ 10): the data flags only.

If the data flags carry `04`, delta-signature metadata follows: a base checksum (formats before
11.2 only), an infinint signature size, its offset when the size is non-zero, and the
result checksum.

## 10. What this writer emits

It writes a single slice in format 11.3, laid out like `dar -at` (no tape marks), so no escape
layer is needed [measured]:

    slice header: 00 00 00 7B | name | 'T' 'T' | 80 00000001 | 00 03 | 80 0000000A | name
    version header: 11.3, letter, "N/A", flags 00, checksum
    file data, one unit per file, uncompressed when compression would not shrink it
    catalogue: name, ".", root entry (permissions 0), d/f entries, z…, checksum; compressed as one unit
    terminator → catalogue
    version trailer: flags 08 + initial offset, checksum
    terminator → trailer
    'T'

uid and gid are written as 0, and permissions and times come from the input when it is on disk.
The inode change time repeats the modification time. Supported methods are stored, gzip, bzip2,
xz, zstd, and lz4 (block framing, 246 660-byte blocks).

**Oracle result** (dar 2.7.13): for every method, `dar -t` exits 0, `dar -l` lists every name,
and `dar -O -x` restores a tree that compares byte-identical to the input, including the
empty-file, empty-directory and 200-byte-name cases. An archive with no entries is accepted too.
`-O` is needed because restoring ownership takes root. dar asks about it for its own archives
too.

## 11. Refused, unverified and open

- **Refused**: encrypted archives (flags 0x20, 0x04 or 0x0400), formats before 9 or after 11.3,
  unknown compression letters and unknown header flags. `List()` then returns the raw slice and a
  `metadata.ini` that names the reason, and does not throw.
- **Delta patches** (saved status 1) are listed, but extraction is refused because the data is a
  binary diff against another archive. Not-saved and inode-only entries are listed and have no
  data to extract.
- **Listed, not materialised**: symlinks, devices, pipes, sockets. EAs and FSAs are skipped
  using their recorded offsets and sizes.
- **Unverified**: formats 9 to 11.2 are decoded by the version rules above (dates, the
  not-saved flag byte, the in-place path, the patch base checksum), but no archive older than
  11.3 was available to measure.
- **Not attempted**: multi-slice writing, tape-mark writing, symlinks and ownership in the writer
  (the archive input model carries neither).

## Sources

- dar documentation, `doc/Notes.html`, "Archive structure in brief" (slice, archive and catalogue
  levels, terminators, escape layer), dar 2.8.6 source tree. [doc]
- libdar 2.8.6 source (GPL), read to derive sections 2–9 and not copied: `real_infinint`,
  `limitint`, `crc`, `datetime`, `header`, `header_version`, `header_flags`, `archive_version`,
  `terminateur`, `escape`, `sparse_file`, `compressor`, `block_compressor`,
  `compress_block_header`, `catalogue`, `cat_*`. [src]
- dar 2.7.13 and 2.8.6 binaries as oracles. [measured]
