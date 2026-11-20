# Now Compress archives: on-disk format

`FileFormat.NowCompress` reads and writes this format. Now Compress (Now Software, Macintosh, early
1990s) has no published description. The reader is a conversion of XADMaster's parser and decoder
(LGPL-2.1-or-later; see *Provenance*). Those two decide what any reader needs. A writer needs
more: the fields the reader skips, the checksums it never checks, and the conventions Now Compress
follows. Those were measured on the eleven public archives listed in
`Compression.Tests/NowCompress/ReferenceVectors/native-oracle-facts.json`: 1 596 files and 1 770
blocks. Each statement below says which of the two it rests on.

All integers are big-endian. Times are seconds since 1904-01-01 (the Macintosh epoch); 0 means
none.

## 1. Archive layout

| Offset | Size | Field |
| --- | --- | --- |
| 0 | 2 | `00 02` |
| 2 | 6 | `00 60 00 01 00 00` or `01 60 00 01 00 00` (10 archives), `00 10 00 00 80 00` (1); the meaning is unknown, and readers ignore it |
| 8 | 4 | number of directory records, folders included |
| 12 | 8 | 0 |
| 20 | 4 | sum of bytes 0–19 |
| 24 | 110 × n | directory records |
| … | … | one stream per file, in directory order, back to back |

The sum at offset 20 holds in every measured archive. Readers do not check it. The first stream
starts right after the last record.

## 2. Directory record (110 bytes)

| Offset | Size | Field |
| --- | --- | --- |
| 0 | 1 | name length, 1–31 |
| 1 | 31 | name in Mac OS Roman; bytes after the name are not cleared |
| 32 | 4 | 0 in every measured record |
| 36 | 2 | flags: 0x10 means folder; 0x02 and 0x04 also occur, with unknown meaning |
| 38 | 16 | Finder info (type, creator, Finder flags, position) |
| 54 | 4 | creation time |
| 58 | 4 | modification time |
| 62 | 4 | last-access time, often 0 |
| 66 | 16 | unknown; readers skip it |
| 82 | 2 | folder: number of records that follow and belong to it |
| 84 | 2 | unknown; readers skip it |
| 86 | 4 | data fork length |
| 90 | 4 | resource fork length |
| 94 | 4 | unknown; readers skip it |
| 98 | 4 | file: offset of its stream |
| 102 | 4 | file: offset just past its stream |
| 106 | 4 | sum of bytes 0–105 of this record |

A folder's record is followed directly by the records of its children, depth first. For a folder,
the fork lengths and stream offsets are not cleared, so they hold arbitrary values. Readers
recognise an archive by its first record: a valid name length, printable name bytes, and a matching
record sum.

## 3. File stream

A file's stream is a block table followed by the blocks. The resource fork comes first, then the
data fork, each cut into blocks of at most 32 KiB of output.

| Size | Field |
| --- | --- |
| 4 | `first`: offset of the first block, equal to the stream offset + table length |
| 8 × k | entries: u16 flags, u16 padding, u32 `next` |
| 4 | the last entry's flags and padding again (arbitrary when the table ends with a fork marker) |
| 4 | checksum |

- **Entries.** Starting from `last = first`, each entry with `next ≠ last` describes one block at
  `last`, `next − last − padding − 4` bytes long, and sets `last = next`.
- **Fork marker.** An entry with `next = last` describes no block. It marks the start of a fork:
  the first block after it starts the data fork.
  - A file with both forks has the pattern *resource blocks, marker, data blocks*.
  - A data-only file starts with the marker.
  - A resource-only file ends with it.

  The marker's flags and padding are leftovers, often a copy of the entry before it.
- **Checksum.** It is the sum of the bytes of `first`, of every entry field, and of the four
  repeated bytes. Readers check it, and XADMaster searches for a shifted table when it fails.
- **Block layout.** Each block is followed by `padding` bytes (1 when the block's length is odd,
  otherwise 0; the value of the byte is arbitrary). Then come 4 bytes: the sum of the block's
  bytes and its padding byte. Readers ignore both. All 1 770 measured blocks follow this.

Readers decode the blocks of all streams as one sequence and cut the files out of it by their fork
lengths. A file with no data has a table with only the marker and no blocks. XADMaster repeats a
stale block when it meets such a table, so every file after it comes out wrong. An empty file is
safe only when no file with content follows it. Our writer places empty files last in their folder
and refuses an archive that would need one earlier. No measured archive contains an empty file.

## 4. Blocks

The flags select the coding of a block:

| Flags | Coding | Seen |
| --- | --- | --- |
| `& 0x20`, `& 0x1F` ≠ 0 | static Huffman, then LZSS | 0 |
| `& 0x20`, `& 0x1F` = 0 | static Huffman only | 2 |
| `& 0x40` | LZSS with Huffman-coded tokens ("new LZSS") | 1 741 (0x40 and 0x140) |
| `& 0x1F` ≠ 0 | LZSS | 5 (0x10) |
| none of these | stored | 22 (0x00 and 0x100) |

The meaning of bit 0x100 is unknown, and it does not change the coding. For a block that starts a
fork, XADMaster restores a saved 32 KiB history before decoding, and saves the history again
afterwards. The new LZSS can reach into that history. Stored blocks and LZSS blocks in the measured
archives never reach outside their own block.

**LZSS** (flags 0x10):
- The block starts with 2 bytes that decoders skip; they are `00 03` in every measured block.
- Then come groups of up to eight items. Each group is led by a flag byte, read most significant
  bit first: 1 means a literal byte, 0 means a match.
- A match is two bytes `b1 b2`:
  - distance = `(b1 & 0xF8) << 5 | b2`, 1 to 8191;
  - length = `(b1 & 7) + 2`.

  When `b1 & 7` is 0, the length is a third byte + 2.
- The block ends with its data. A flag byte is always followed by at least one item.

The Huffman and new-LZSS codings are as in the reader (`NowCompressArchive.cs`).

## 5. What the writer emits

- The header `00 02 00 60 00 01 00 00`, the record count and the header sum. Records as in §2,
  with every unknown field 0. Finder info is 0, and the
  creation and modification times are both the input's modification time.
- One stream per file:
  - every 32 KiB block is LZSS-packed if that makes it shorter, otherwise stored (flags 0);
  - padding and block sums as measured;
  - a fork marker that copies the preceding entry's flags and padding, or 0 when it is first;
  - the last entry's flags and padding repeated before the checksum.
- The LZSS encoder uses greedy matching over a hash chain within the block.

## Provenance

- **Decoding.** XADMaster (`XADNowCompressParser.m`, `XADNowCompressHandle.m`), revision
  `7cb9ee0abbb163f261e4cb74501e15067032319c`, LGPL-2.1-or-later, converted into
  `NowCompressArchive.cs` (notice in `THIRD-PARTY-NOTICE.XADMaster.txt`).
- **Measured.** These facts come from the eleven dexvert sample archives pinned in
  `native-oracle-facts.json`:
  - the header and record sums;
  - the fork-marker placement;
  - padding, block sums and the repeated four bytes;
  - the `00 03` LZSS prefix;
  - block counts per coding.
- **Verified.** Native unar 1.10.7, built from that XADMaster revision, extracts every fork of the
  archives our writer produced, with both LZSS and stored blocks, to the input bytes
  (`Compression.Tests/NowCompress/ReferenceVectors/README.md`). It also showed that an empty file
  followed by another file breaks the files after it. Now Compress itself was not available to
  check our archives.
