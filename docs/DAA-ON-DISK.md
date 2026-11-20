# DAA (PowerISO "Direct Access Archive") — on-disk notes

`FileFormat.Daa` is implemented from this page. Two sources fed it:

- Images written by PowerISO for Linux 1.3 (`poweriso-1.3.tar.gz`, freeware,
  run as a black box), read field by field.
- Version 0x110 images written by PowerISO 9.5 for Windows (`piso.exe`, the
  trial's command line, under wine), read the same way. PowerISO 9.5's own
  conversion back to ISO is the expected output.
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
plus a body, where `length` includes the 8). PowerISO 1.3 writes:

| Type | Length | Meaning |
| --- | --- | --- |
| 1 | `0x3C` | multi-volume image; the body holds the volume count and the volume size, which a reader does not need |
| 2 | `0x1FF` | follows type 1; zero apart from its own fields in every measured image |
| 3 | `0x90` | password: u32 password type (0), u32 check CRC, 128-byte key |

A reader skips every record by its length and acts only on types 1 and 3.

## Split images

`-volsize` cuts the image file into volumes named `<name>.part01.daa`,
`<name>.part02.daa`, … (daa2iso also accepts `<name>.001.daa`). They behave as
one file:

- The first volume is a normal DAA file with a type-1 descriptor. The header's
  DAA size (0x30) is the sum of all the volume file sizes.
- Each later volume starts with a 100-byte (`0x64`) header. Its signature is
  `"DAA VOL"` + 9 NULs and its field at 0x10 is `0x64`, the start of its data.
  The rest of that header was not needed.
- The chunk data continues from the end of one volume to the data start of the
  next, so a chunk can straddle two volumes. Chunk offsets count through that
  joined data.

## Password-protected images

The type-3 descriptor holds a check CRC and a 128-byte key. The password turns
into 128 nibble permutations, table *Tₙ* for each block size *n* = 1 … 128.
Each table sends the 2*n* nibbles of an *n*-byte block to new positions:

- Input byte *i* has its low nibble at position 2*i* and its high nibble at
  position 2*i*+1.
- Table entry *t* = *Tₙ*[*k*] for nibble *k* says where that nibble goes: into
  output byte *t* div 2, as the high nibble if *t* is odd, otherwise as the
  low nibble.

**Check.** Apply *T₁₂₈* to the 128-byte key. The CRC-32 of the result equals
the check CRC exactly when the password is right.

**Chunks.** Before inflating, each stored chunk (deflate data and CRC trailer)
is undone block by block: every full 128 bytes with *T₁₂₈*, then a final
partial block of *r* bytes with *Tᵣ*.

**Building *Tₙ*.** Let *m* = 2*n* and *p₀*, *p₁*, … be the password bytes. Each
of the *m* positions is used exactly once. "Step *k*" below means: move the
cursor forward to the *k*-th unused position, wrapping at *m* to the given
restart point.

- *m* ≤ 64: the cursor starts at min(*p₀* ≫ 5, *m* − 1). Repeat *m* times: step
  11 (restart 0), take that position.
- *m* > 64:
  1. Take position (*p₀* ≫ 5) + 32 first. The cursor starts at *p₀* ≫ 5.
  2. Then take *m* − 33 positions. For each one, step *c* with restart 32,
     where *c* is the next password byte *p₁*, *p₂*, … When the password is
     used up, or the byte is 0, *c* = 11.
  3. Take the remaining 32 positions by step *p₀* & 7 (7 when that is 0),
     restart 0.
  4. If *p₀* & 24 is not zero, reorder the finished table. Walk it
     cyclically, skipping entries already taken, with a cursor that starts at
     index 0 and moves *p₀* & 24 entries each time. The entries picked, in
     that order, form the new table.

Measured on PowerISO 1.3 images with the password `Secret-30`: the check CRC
matches, and every chunk inflates to the ISO PowerISO was given.

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

## Version 0x110

PowerISO 9.5 writes 0x110 for `-method better` and `-method best`
(`normal` still gives 0x100). Three header fields change meaning:

| Field | 0x110 meaning |
| --- | --- |
| 0x18 data offset | the low 24 bits are the offset; the high byte holds flags |
| 0x24 chunk size | `(value & 0xFFF) << 14` is the chunk size (`best`: 4 MiB, `better`: 512 KiB); the other bits are flags. `0x100000` means LZMA chunks may occur |
| 0x38 header data | byte 5: the kind width in bits (low 3 bits) and the length width (high 5 bits, plus 10; 0 means the number of bits needed for the chunk size); byte 6: non-zero for an x86 branch filter behind LZMA; bytes 7-11: the 5 LZMA properties (`5D 00 00 00 01`, lc 3, lp 0, pb 2, 16 MiB dictionary) |

The chunk table is a stream of fields read least-significant bit first: for
each chunk, the length minus 5 in the length width, then the kind in the kind
width. Kind 0 is LZMA and kind 1 is raw deflate. A length that reaches the chunk
size marks a stored chunk.

Every chunk, of any kind, ends with the CRC-32 of the bytes before it, as in
0x100. LZMA chunks are raw LZMA with the header's properties and no end marker.
A decoder must stop at the chunk's size: the stream decodes 4 extra bytes from
the CRC. With the filter flag set, the decoded chunk passes through the x86 BCJ
decoder (LZMA SDK `x86_Convert`, start offset 0, fresh state per chunk).

Passwords and volumes work as in 0x100. The password descriptor is the same,
and the cipher covers each chunk's bytes. Volumes are named `name.part01.daa`,
`name.part02.daa`, and so on.

All of this is measured: `better` images hold LZMA and deflate chunks, `best`
images one LZMA chunk, split sets also hold stored chunks, and every one
decodes to the ISO PowerISO 9.5 converts it to.

## Not supported (refused with a message)

- **0x110 options no PowerISO 9.5 image uses:**
  - a zlib-compressed chunk table (chunk-size flag `0x4000`);
  - an obfuscated table (`0x8000000`) or obfuscated table bits (`0x20000`);
  - permuted deflate block types (bits 23-24);
  - flags `0x80000`, `0x80000000` and `0x10000000`.
  daa2iso describes them, but no sample could verify them.
- **gBurner GBI.** It has the same layout, but the chunk table is additionally
  offset by the low byte of the header CRC and XORed with `table length / 4`.
  There is no sample.
