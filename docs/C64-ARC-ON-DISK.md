# C64 ARC and SDA on disk

Chris Smeets' ARC for the Commodore 64/128 (ARC 2.x, 1987–1990) and its self-dissolving archives
(SDA). The format has no published specification. This page was written from the behaviour of
cbmconvert 2.1.5 (Marko Mäkelä; GPL-2.0, so used as a reference and an oracle only, not as source)
and from measurements on four SDA files from the dexvert sample set (`LNK128.SDA`,
`convt64.sda`, `spice.sda`, `tetrix.sda`). Those files use only modes 0 (stored) and 3
(crunched): for them, cbmconvert's output and every entry's checksum match what we decode. Modes
1, 2, 4 and 5 are covered only by archives the tests build from this description, which
cbmconvert extracts to the same bytes as we do; no real archive in those modes has been seen.

All multi-byte numbers are little-endian.

## 📦 Container

A plain archive starts with its first entry header. An **SDA** is a Commodore program file: a
2-byte load address (`$0801` for the C64, `$1C01` for the C128), then one BASIC line
`<next-line pointer:2> <line number:2> $9E "(" <address digits>`, the 6502 extractor, and the
archive. The line number gives the extractor's size in 254-byte disk-block payloads, so the
first entry starts at `(line − 6) × 254` bytes from the start of the file. The one exception is
the C128 extractor of SDA 2.32 (line number 15, first address digit `7`), which starts one byte
earlier. Measured: line 13 → 1778 (`convt64`, `tetrix`), line 15 C64 → 2286 (`spice`), line 15
C128 → 2285 (`LNK128`).

Entries follow one another. Each entry occupies `blocks × 254` bytes, counted from its first header
byte; the next header starts there. The archive ends at end of file or at the first header that is
not valid (version not 1 or 2, name longer than 16). Files sent over XMODEM end in padding.

## 🧾 Entry header

| Offset | Size | Field |
| ---: | ---: | --- |
| 0 | 1 | version: 1 (ARC 1.x) or 2 |
| 1 | 1 | mode: 0 stored, 1 packed (RLE), 2 squeezed (Huffman), 3 crunched (LZW + RLE), 4 squashed (Huffman + RLE), 5 one-pass crunch; version 1 only uses 0–2 |
| 2 | 2 | checksum of the original data |
| 4 | 3 | original size |
| 7 | 2 | blocks: stored size in 254-byte units, header included |
| 9 | 1 | CBM file type: `P` program, `S` sequential, `U` user, `R` relative |
| 10 | 1 | name length n (≤ 16) |
| 11 | n | name, PETSCII |
| 11+n | 1 | version 2 only: record length (relative files) |
| 12+n | 2 | version 2 only: date, MS-DOS format |
| … | 1 | mode 1 only: the RLE marker byte. In all other modes the marker is `$FE` |
| … | — | modes 2 and 4: the Huffman table (see below), in the bit stream |

Compressed data follows immediately.

## 🔁 Run-length layer

It is used in modes 1, 3, 4 and 5, applied after the entropy layer is decoded. A decoded marker
byte is followed by a count and a value. The value repeats `count` times, where count 0 means 255
in version 1 and 256 in version 2. Any other byte stands for itself.

## 🧮 Bit stream

Bits are taken from each byte least-significant first, and a byte is fetched only when the next
bit is needed. Each entry starts a new bit stream, after its byte-aligned header fields.

### Huffman (modes 2 and 4)

The table holds 256 records, for byte values 0 to 255 in order. Each record is a 5-bit code length
L, assembled LSB first (L ≤ 24, and 0 means the value does not occur), followed by L code bits,
where the i-th bit read is bit i of the code. To decode, read bits one at a time, building the
value the same way: the i-th bit becomes bit i. After each bit, the symbol whose length equals
the bits read and whose code equals the value is the decoded byte.

cbmconvert 2.1.5 sorts this table by code length before decoding. Its shell sort crashes
(segmentation fault) on any table where an entry has to move, so it can serve as an oracle only
for tables whose lengths already fall as the byte value rises.

### LZW (modes 3 and 5)

LZW as in Welch (IEEE Computer, June 1984), with codes read most-significant bit first: each code
is `code = code << 1 | bit`, repeated for the current width.

- Codes 0–255 are literals, 256 ends the entry, and 257 is reserved. The first new code is 258.
- The width starts at 9 bits. It counts down on every code read, the first one included. After
  254 codes at 9 bits it becomes 10; after 512 more it becomes 11; after 1024 more it becomes 12,
  and it stays there.
- The table is never reset. It stops growing at 4096 entries.
- After each string except the first, the decoder adds the previous code plus the first byte of
  the current string. A code that is not yet defined stands for the previous string plus that
  string's first byte.
- Mode 5 does not know its size up front. After the end code it stores, in the same MSB-first
  bit order, a 16-bit checksum, a 24-bit size and 16 unused bits. These replace the header's
  checksum and size. Its `blocks` field is not used: the entry's length is the number of bytes
  consumed, rounded up to 254.

## ✅ Checksum

The checksum is 16 bits and wraps around, taken over the decoded output:
- version 1 adds each byte;
- version 2 adds `byte XOR k`, where k is a byte counter that is 1 for the first output byte and
  wraps at 256.

## 🔤 Names

Names are PETSCII. On extraction, `$41`–`$5A` become a–z, `$61`–`$7A` and `$C1`–`$DA` become A–Z,
and `/` becomes `.`. The CBM file type is added as an extension (`.prg`, `.seq`, `.usr`, `.rel`),
which matches cbmconvert's native output names.
