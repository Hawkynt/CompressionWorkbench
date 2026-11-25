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
**encryption** layer (section 12), an optional **escape** layer (tape marks), a **compression**
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
- An inode's EA and FSA fields appear in the copy without offsets and checksums: the EA size
  (EA full), the FSA families (FSA not none), the FSA size (FSA full) [measured].
- Saved EAs follow the entry as mark `E`, the EA block (section 9.2) and mark `r` with the EA
  checksum; saved FSAs then as `S`, the FSA block and `s` with its checksum. For a file they come
  after the data's `R`; for a directory right after its copy, before its children [measured:
  dar 2.7.13 on an ext4 tree with user EAs, `chattr +d` and hard links].
- A hard-linked inode (section 9.3): its first name is copied as the whole `m … >` entry with the
  inner entry's copy, then its data and attributes as for a file; each further name is the copy
  `m`, name, label, `X` alone.

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
| `m` hard link | name, infinint label, `>` + a full entry the first time; `X` for later names (section 9.3) |
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

### 9.1 Where the EA and FSA blocks are

The offsets point into the archive level (the escaped stream with tape marks, the clear stream of
a block cipher), like file data offsets [measured]. Neither *size* is the stored length: the EA
size is the sum of the attribute names' and values' lengths (`user.dir` = `d` gives 9, the block
is 20 bytes), and the FSA size is likewise dar's own measure (61 for a 67-byte block of 13
attributes). The catalogue records no stored length at all [measured: dar 2.7.13]. A block ends:

- with tape marks, at the next mark (`r` after an EA block, `s` after an FSA block);
- otherwise where the next thing the catalogue points at begins — file data, another EA or FSA
  block, or the catalogue. dar writes them back to back: a file's data, its EA block, its FSA
  block; a directory's EA and FSA blocks before its children's [measured].

The **EA block passes through the compression layer** as its own unit, like a file's data, under
the archive's algorithm and block size, even when it grows (20 bytes became a 25-byte zlib
stream); the **FSA block never does**. Both checksums cover the clear (uncompressed, unescaped)
block: the EA checksum of `user.dir` = `d` is `81 5B 96 68` in a stored and in a gzip archive
alike. dar writes them 4 bytes wide [measured].

### 9.2 EA and FSA block contents

Read for this document but carried by the editor as opaque bytes [measured: dar 2.6.16
(format 10.1, EAs) and 2.7.13 (11.3, EAs and FSAs) on the same tree]:

- **EA block**: an infinint count, then per attribute its full name as a string (`user.color`)
  and its value as an infinint length and the bytes. An empty value is length 0.
- **FSA block**: an infinint count, then per attribute a family letter (`l` Linux ext2/3/4), a
  two-letter nature and a value. On ext4 dar 2.7.13 writes 13: `aa` (the birth time, a date
  as in section 2.4) and `ba` to `bl`, the twelve `chattr` flags, each `T` or `F` (`bc` is
  `chattr +d`, no dump). The families infinint of the inode was 2 on every ext4 inode.

### 9.3 Hard links

All names of one inode share an infinint **label**, numbered from 0 in the order dar meets them
[measured]. The first name in catalogue order is written `m` (saved status 3: `6D`), the name,
the label, `>`, and then a complete entry of the inode — signature (`66` for a file), the same
name again, the inode with its EA and FSA fields, the file part. Every further name is `6D`, the
name, the label and `X`, nothing else. The data, EA and FSA blocks exist once, written where the
first name is. A label may only be referred to (`X`) after its `>` entry [measured: `h3`, `h1`,
`h2` of one inode written `m h3 0 > f h3 …`, `m h1 0 X`, `m h2 0 X` by dar 2.7.13].

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
- **Encryption** (a password): section 12, with every cipher and key derivation dar offers;
  argon2 with 10 000 iterations by default, as dar 2.7.13 writes.
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

- **Refused**: archives whose key is encrypted for GnuPG recipients (flag 0x04: an OpenPGP
  message this repository does not decode), block-cipher archives without recorded key-derivation
  parameters (formats before 10), formats before 8 or after 11.3, unknown compression letters and
  unknown header flags. `List()` then returns the raw slice and a `metadata.ini` that names the
  reason, and does not throw. A missing or wrong password is reported as such instead.
- **Crypto block size**: dar's `-#` is not recorded in the archive; the reader assumes dar's
  default of 10 240 clear bytes, so an archive written with another `-#` does not decrypt.
- **Delta patches** (saved status 1) are listed, but extraction is refused because the data is a
  binary diff against another archive. Not-saved and inode-only entries are listed and have no
  data to extract.
- **Listed, not materialised**: symlinks, devices, pipes, sockets. EAs and FSAs are not
  extracted; edits carry them over (section 13).
- **Memory when reading**: members stream to disk in constant memory, except xz in streaming
  mode: the xz decoder holds one whole .xz stream, which is the whole file when dar wrote it.
- **Not written for new files**: hard links, devices, pipes, sockets, EAs and FSAs; the archive
  input model carries none of them. Edits keep those of the entries they carry over.

## 12. Encryption

dar encrypts with a password (`-K [algo:]pass`) by scrambling or with one of five block ciphers,
or for GnuPG recipients [doc]. Everything below was read from libdar's source [src] and checked
against dar 2.7.13 (libgcrypt and libargon2 build) both ways [measured].

**Header fields.** Flag 0x20 marks an encrypted archive; from format 9 it is followed, after the
initial offset, by the cipher letter: `s` scrambling, `b` Blowfish, `a` AES-256, `t` Twofish-256,
`p` Serpent-256, `c` Camellia-256. Flag 0x04 adds the GnuPG-encrypted key (infinint size, bytes),
then the reference slicing, then flag 0x0400's key derivation: an infinint salt size, the salt, an
infinint iteration count and a hash letter, `a` argon2, `5` SHA-512, `1` SHA-1, `m` MD5 — then the
block size of 0x0800. dar 2.7.13 writes header flags `05 20` (0x0420) with a 32-byte salt
(16 for Blowfish), 10 000 iterations and `a`; the trailer adds 0x08 and the initial offset
[measured].

**Key.** PBKDF2-HMAC over the password bytes with the salt and count, or argon2id with
t = count, m = 100 KiB, p = 1. The key is 56 bytes for Blowfish and 32 for the others [src]. A
Blowfish key that leaves a value twice in one S-box is rejected by libgcrypt as weak; dar then
draws a new salt [src], and so does the writer here.

**Zones.** The version header stays clear. The encrypted zone runs from the initial offset to the
version trailer; the trailer and the second terminator stay clear after it [src][measured]. Its
clear content is: an elastic buffer of 1–51 200 bytes, the escape/compression layers (file data,
catalogue), the first terminator, and a final elastic buffer. For a block cipher every offset in
the catalogue and the first terminator counts in this clear stream from 0 at its start; for
scrambling it stays an archive-level offset [src][measured].

**Elastic buffer.** One byte: `X`. Two: `FF FE`. More: random filler, `FF`, the length in base
254 least significant digit first, `FE`, filler; the filler never holds `X`, `FE` or `FF`. It is
read backwards from its end: the last `X` or `FE`, then digits up to `FF` [src][measured].

**Block ciphers** (CBC). The clear stream is cut into blocks of 10 240 bytes (`-#`). Block *n*
(from 0) is padded with an elastic buffer to the next whole cipher block *beyond* it — so a full
block of 10 240 bytes takes 10 256 bytes under the 16-byte ciphers and 10 248 under Blowfish — and
encrypted under the initialisation vector E_essiv(*n* as a big-endian cipher block). The ESSIV
key is SHA-256 of the key under AES-256, or for Blowfish SHA-1 of the key under Blowfish
[src][measured: libdar's own self-test vectors, and dar 2.7.13]. The final elastic buffer is
sized so the last block is full: dar tells the encrypted blocks from the clear trailer by the
short read at the end [src].

**Scrambling.** Each byte of the zone plus the password byte at its archive-level position
modulo the password length; no key derivation [src][measured].

## 13. Editing in place

An edit rewrites the archive with its own settings: format 11.3, the compression letter and block
size, tape marks, the slice sizes of slice 1's header, and the encryption (cipher, KDF hash and
count, a new salt). Kept entries go across unchanged — their inode bytes, and for files the stored
(compressed, unescaped) data, compression letter, data flags (holes included) and checksum — after
each kept file has passed its checksum; added files are written as by the writer. The new archive
is written to temporary files first; only then does it replace the old slices, and slices it no
longer needs are deleted.

**EAs, FSAs and hard links** travel too. Each EA and FSA block is read where section 9.1 says it
ends, checked against its checksum and written again after its entry's data (a directory's
before its children), the EA block recompressed as its own unit under the archive's settings,
the FSA block as it is; the inode keeps its flag, ids, permissions, dates, sizes, families and
checksums, with the new offsets. With tape marks the blocks go between `E`/`r` and `S`/`s` and
the inline copies carry the fields of section 6. A hard-linked file keeps one inode: its names
are numbered again from label 0, the first name the edited catalogue meets carries the inode
(`>`), the others refer to it (`X`), and a group an edit leaves with one name becomes a plain
file. Removing the name dar had written the inode under is fine; the inode moves to the next.

Refused, with the archive untouched: entries whose data is not in the archive (differential
archives), devices, pipes and sockets, hard-linked entries other than files, EA blocks of formats
before 10.1 and FSA records of formats before 11.3 (layouts not measured there), a root directory
with saved attributes (dar never writes one), delta signatures, formats before 9, lzo as the
archive's compression, and an edit through a slice the edited set would no longer have.

dar 2.7.13 tests (`dar -t`, sequentially too with tape marks) and restores byte for byte what an
edit leaves of its own archives — default, `-at -zgzip`, block mode, xz, slice sets, AES-256,
scrambled slice sets, sparse files carried over — and of ours [measured]. For archives dar made
from an ext4 tree with user EAs on files and a directory, `chattr +d` on a file and a directory,
a file with three names, a symlink, an empty file and an empty directory — default, `-at`, gzip,
bzip2 without tape marks, zstd block mode, xz, an lz4 slice set and AES-256 — an edit that
replaces a file, adds one, removes one and removes the name dar wrote the hard-linked inode under passes
`dar -t` (and `--sequential-read` with tape marks), and `dar -x` restores a tree whose contents,
modes, file times, `getfattr` EAs, `lsattr` flags and hard-link groups equal the source edited the
same way (`DarEditAttributesOracleTests`) [measured].

## Sources

- dar documentation, `doc/Notes.html`, "Archive structure in brief" (slice, archive and catalogue
  levels, terminators, escape layer), dar 2.8.6 source tree. [doc]
- libdar 2.8.6 source (GPL), read to derive sections 2–9 and not copied: `real_infinint`,
  `limitint`, `crc`, `datetime`, `header`, `header_version`, `header_flags`, `archive_version`,
  `terminateur`, `escape`, `sparse_file`, `compressor`, `block_compressor`,
  `compress_block_header`, `catalogue`, `cat_*`; libdar 2.7.13 (GPL), read the same way for
  sections 12 and 13: `crypto_sym`, `tronconneuse`, `elastic`, `scrambler`, `header_version`,
  `macro_tools`, `archive_options`, `crypto`, `archive_aux`. [src]
- dar 2.7.13 and 2.8.6 binaries as oracles; for encryption the 2.7.13 package, the 2.8.6 build
  having no libgcrypt. [measured]
