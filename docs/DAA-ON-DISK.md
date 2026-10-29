# DAA (PowerISO "Direct Access Archive") — on-disk notes

`FileFormat.Daa` is implemented from this page. Two sources fed it:

- Images written by PowerISO for Linux 1.3 (`poweriso-1.3.tar.gz`, freeware,
  run as a black box), read field by field.
- Luigi Auriemma's daa2iso 0.1.7e (GPL-2.0). It was read as a description of
  behaviour only; no code was taken from it.

PowerISO is also the oracle in `Compression.Tests/Daa/DaaTests.cs`. It
converts our images back to identical ISOs, and we read the images it writes.

All integers are little-endian.

## Header (76 bytes)

| Offset | Size | Field |
| --- | --- | --- |
| 0x00 | 16 | signature: `"DAA"` + 13 NULs. `"DAA VOL"` marks a later volume of a split image; `"GBI"` is gBurner's variant |
| 0x10 | 4 | offset of the chunk table (`0x4C` when there are no descriptors) |
| 0x14 | 4 | format version: `0x100`, or `0x110` for newer PowerISO |
| 0x18 | 4 | offset of the chunk data |
| 0x1C | 4 | always 1 |
| 0x20 | 4 | always 0 |
| 0x24 | 4 | chunk size, the decoded bytes per chunk (PowerISO 1.3: `0x10000`) |
| 0x28 | 8 | ISO size |
| 0x30 | 8 | DAA file size; if it differs from the file length, the image is split |
| 0x38 | 16 | header data: zero for 0x100; carries the table-bit, LZMA and obfuscation parameters for 0x110 |
| 0x48 | 4 | CRC-32 (zlib polynomial, standard init and final inversion) of bytes 0x00–0x47 |

## Descriptors

From `0x4C` up to the chunk table there are `{type, length}` records (8 bytes
plus a body, where `length` includes the 8). Type 1 marks a multi-volume image.
Type 3 marks a password-protected image: its body holds the password type, the
password CRC and a 128-byte key. Both are refused.

## Version 0x100 chunk table

There is one 3-byte entry per chunk. The compressed length is
`b0 << 16 | b1 | b2 << 8`. Note the order: the high byte comes first, then the
low byte, then the middle byte. Chunks follow each other from the data offset.

Each chunk is a **raw deflate** stream (no zlib header) that decodes to the
chunk size, followed by **4 bytes: the CRC-32** (zlib polynomial, little-endian like every other field)
of the deflate bytes. This trailer is measured, not taken from daa2iso, which
never reads it. Every chunk PowerISO 1.3 wrote carries it, and PowerISO
rejects images whose chunks lack it ("The file format is invalid or
unsupported"). The last chunk decodes to the remainder, or more; only up to the
ISO size is kept. PowerISO 1.3 writes nothing else: random data is stored in
deflate's own stored blocks, not as an uncompressed chunk.

## Not supported (refused with a message)

- **Version 0x110.** The chunk table is a bit-packed stream of
  `(length − 5, type)` pairs. It can be zlib-compressed and XOR-obfuscated, the
  deflate block-type numbering is permuted, chunks can be LZMA with an x86 BCJ
  filter, and a chunk whose length reaches the chunk size is stored. No
  PowerISO build available here writes 0x110, so none of this could be
  verified. It stays refused rather than guessed.
- **gBurner GBI.** It has the same layout, but the chunk table is additionally
  offset by the low byte of the header CRC and XORed with `table length / 4`.
  There is no sample.
- **Split and password-protected images.**
