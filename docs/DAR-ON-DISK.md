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
| dar 2.7.13 (libdar 6.7.1, Ubuntu 24.04 package) | WSL and CI; `Compression.Tests/Dar/DarExternalConformanceTests.cs` runs it both ways |
| dar 2.8.6, built from the SourceForge source tarball | WSL; the same suite with `CWB_DAR=/opt/cwb-dar/2.8.6/bin/dar`; the `single`, `sliced` and `dar-2.8.6-*` vectors |
| dar 2.4.24, 2.5.22, 2.6.0, 2.6.16, 2.7.0, 2.7.1, 2.7.10, built from source | the `dar-<release>-*` vectors (`ReferenceVectors/make-legacy-fixtures.sh`) |

Which release writes which archive format, read from each release's own version header
[measured]:

| Format | Written by (built and measured) |
| --- | --- |
| 8.1 | 2.4.24 |
| 9.0 | 2.5.5, 2.5.22 |
| 10.0 | 2.6.0 |
| 10.1 | 2.6.16 |
| 11.0 | 2.7.0 |
| 11.1 | 2.7.1, 2.7.8 |
| 11.2 | 2.7.10 (dar's ChangeLog: 2.7.9 to 2.7.11) |
| 11.3 | 2.7.13, 2.8.6 |

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
(nanoseconds). Format 8 has no unit byte: a date is a bare infinint of seconds [measured: 2.4.24].
Formats 9 and 10 write `u`, format 11 writes `n` for real timestamps, and every format with a unit
byte writes `s 0` for ones it does not know [measured: 2.5.22 to 2.8.6].

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
  slice carries only type 3. With `-s` the list is types 1 and 3; type 2 comes first, and only
  when `-S` differs from `-s` (`-S 16k -s 16k` writes no type 2). Every slice of a set carries
  the same list [measured: 2.4.24, 2.7.13, 2.8.6].
- **slice sizes**: every slice but the last is exactly the `-s` size (the first, the `-S` size),
  header and trailer included; the last is whatever is left. dar never starts a slice it has
  nothing to put in [measured].
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

With tape marks the archive level reads, in order [measured: `tree.1.dar`, `tree-gzip.1.dar`]:

    version header | D data name (10) | P in-place path (format ≥ 11.1) | entries … | C catalogue | terminators, trailer

and every entry of the catalogue walk (the root excluded) is written inline as a mark `F`, a
copy of the entry and a 2-byte checksum of that copy:

- The copy is the catalogue entry (signature, name, inode with its EA/FSA fields) minus what is
  only known once the data is written: a file gives its size, its data flags and its compression
  letter, but no offset, stored size or data checksum. Its data follows the copy at once (the
  offset the catalogue records), then a mark `R` and the data checksum (an infinint width and the
  ring), even for an empty file (`R 80 00000001 00`).
- A directory's copy ends in `7A`, an empty child list. Its children follow as their own `F`
  entries, and the end of the directory is one more `F` whose copy is the single byte `7A`
  (checksum `80 00000002 7A 00`). The root's end is not written; `C` follows the last entry.
- A symlink's copy ends in its target string; a pipe's ends after the inode.
- With FSA, a mark `S`, the FSA block and `s` with its checksum follow the entry; with EA, `E`
  and `r`.

The marks and copies are never compressed; each file's data in between is a compression unit as
in section 7. The 2-byte checksum covers the copy from its signature byte [measured: `63 86` for
the fifo of `tree.1.dar`]. The direct-access reader never needs the copies; dar's sequential
reading (`--sequential-read`) lives on them, and accepts copies written this way from our writer
[measured: dar 2.7.13 and 2.8.6, `dar -t --sequential-read` and `dar -x --sequential-read`].

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

The in-place path is the `-R` root and must be absolute or `.` [src]. Formats before 11.1 do not
have it [measured: 2.7.0 writes none, 2.7.1 does]. The checksum covers everything before it,
uncompressed and unescaped.

Across formats 8.1 to 11.3 the entry layouts below are the same; only the dates differ
(section 2.4) [measured: the `dar-<release>-*` vectors, every one read back byte for byte].

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

Format 11.3, in one slice or a slice set, with or without tape marks:

    slice header: 00 00 00 7B | name | 'T' 'T' | 80 00000001 | 00 03 | 80 0000000A | name
                  (sliced: 'E' 'T' | count | [00 02 first size] | 00 01 slice size | 00 03 name)
    version header: 11.3, letter, "N/A", flags 00 (10 with tape marks), checksum
    [D name | P "."]                                   tape marks only
    per entry, depth first: [F copy + checksum] data [R checksum]
    [C] catalogue: name, ".", root entry (permissions 0), d/f/l entries, z…, checksum; one unit
    terminator → catalogue
    version trailer: flags 08 (18 with tape marks) + initial offset, checksum
    terminator → trailer
    trailer byte of the last slice: 'T'

- **Slices** (`SliceSize`, `FirstSliceSize`, dar's `-s` and `-S`): payload runs into slice
  `n + 1` as soon as slice `n` holds its size minus header and trailer; every slice but the last
  ends in `N`. The descriptor puts slice `n` beside the first, which must be named
  `basename.1.dar`.
- **Tape marks** (`TapeMarks`): the layout of section 6, data escaped on the way out.
- **Streaming**: file data goes from its source to the slices as it is read, so memory does not
  grow with file size; only the catalogue is held. Files up to 4 MiB are compressed in memory
  first and stored as they are when compression would not shrink them, as dar does. Larger files
  are compressed on the fly and kept compressed whatever the outcome; dar accepts either letter
  per file. The data checksum is computed while streaming.
- **Entries**: directories, regular files and symlinks (an on-disk input that is a symlink is
  stored as one, with its target). uid, gid, permissions and the access, modification and change
  times come from the input's metadata, then from the file on disk, else uid and gid 0, 0644/0755
  and the current time.
- **Methods**: stored, gzip (zlib stream), bzip2, xz, zstd, and lz4 (block framing, 246 660-byte
  blocks), in streaming mode or, with `CompressionBlockSize`, in block mode (flag 0x0800,
  section 7).
- **Large xz and zstd files**: this repository's xz and zstd encoders close their stream or frame
  every 64 MiB of input and open another. dar does not read on past the first: `dar -x` restored
  the first 67 108 864 bytes of a 70 MiB file written that way, and `dar -t` still passed
  [measured: dar 2.7.13]. So when any file is larger than that, an xz or zstd archive is written
  in block mode with 8 MiB blocks, each one stream or frame.

**Oracle result**, dar 2.7.13 and dar 2.8.6, for every method, with and without tape marks, in
one slice and in slice sets with a bigger and with a smaller first slice, and for files taking
the streaming path: `dar -t` exits 0 (with tape marks also `dar -t --sequential-read`), `dar -l`
lists every name, and `dar -O -x` (and `--sequential-read`) restores a tree that compares
byte-identical to the input, including empty files, empty directories and 200-byte names. `dar
-l` shows symlink targets, permissions and the uid/gid written; `dar -x` restores the symlinks,
permissions and modification times, and ownership when run as root. An archive with no entries is
accepted. A 5 GiB file, stored and sliced or gzip-compressed with tape marks, passes `dar -t`
(advisory `Performance` test `DarLargeFileTests`).

## 11. Refused, unverified and open

- **Refused**: encrypted archives (flags 0x20, 0x04 or 0x0400), formats before 8 or after 11.3,
  unknown compression letters and unknown header flags. `List()` then returns the raw slice and a
  `metadata.ini` that names the reason, and does not throw. dar's ciphers (blowfish, AES-256,
  twofish, serpent, camellia) sit under a key derivation dar alone defines; nothing here derives
  that layer, so encryption stays refused in both directions.
- **Delta patches** (saved status 1) are listed, but extraction is refused because the data is a
  binary diff against another archive. Not-saved and inode-only entries are listed and have no
  data to extract.
- **Listed, not materialised**: symlinks, devices, pipes, sockets. EAs and FSAs are skipped
  using their recorded offsets and sizes.
- **Memory when reading**: members stream to disk in constant memory, except xz in streaming
  mode: the xz decoder holds one whole .xz stream, which is the whole file when dar wrote it.
- **Not written**: hard links, devices, pipes, sockets, EAs and FSAs; the archive input model
  carries none of them.

## Sources

- dar documentation, `doc/Notes.html`, "Archive structure in brief" (slice, archive and catalogue
  levels, terminators, escape layer), dar 2.8.6 source tree. [doc]
- libdar 2.8.6 source (GPL), read to derive sections 2–9 and not copied: `real_infinint`,
  `limitint`, `crc`, `datetime`, `header`, `header_version`, `header_flags`, `archive_version`,
  `terminateur`, `escape`, `sparse_file`, `compressor`, `block_compressor`,
  `compress_block_header`, `catalogue`, `cat_*`. [src]
- dar 2.7.13 and 2.8.6 binaries as oracles. [measured]
