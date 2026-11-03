# FreeArc Tornado (`tor`) stream

The layout of the stream FreeArc 0.67 stores for a `tor:…` stage, and the decoding rules
`FreeArcTornado` implements.

## 🔎 Provenance

- **Licence.** Tornado (Bulat Ziganshin, Joachim Henke) is GPL-2.0 like the rest of FreeArc's
  sources, so this is rung 2 of the sourcing ladder: `Compression/Tornado/Tornado.cpp`,
  `LZ77_Coder.cpp`, `EntropyCoder.cpp` and `DataTables.cpp` of the 0.67 snapshot mirrored at
  `github.com/mrkaban/FreeArc-0.67` were read to write this document, and the decoder was written
  from this document. The constant tables below (extra-bit counts, the 14-bit range, block
  sizes) are copied as the format requires.
- **Oracle.** FreeArc 0.67 `Arc.exe`; see `Compression.Tests/FreeArc/ReferenceVectors/`.

## 🧱 Stream

| Bytes | Meaning |
|---|---|
| 1 | coder: 1 = byte, 2 = bit, 3 = Huffman, 4 = arithmetic (0, storing, is never written) |
| 1 | minimum match length `MINLEN` |
| 4 | dictionary size, little-endian (only bounds distances) |
| … | the coded LZ77 stream, ending with the end marker |

The decoder produces a sequence of literals and matches. A match copies `len` bytes from `dist`
bytes back in the output, byte by byte (copies may overlap). The end marker is a match of length
`IMPOSSIBLE_LEN` = 0x3FFFFFFF at distance `IMPOSSIBLE_DIST` = 0x3FFFFFFF. A distance larger than
the output so far is an error.

### Byte coder (1)

Before every group of 16 elements a 32-bit little-endian flag word; element *k* of the group uses
bits 2*k*..2*k*+1: 0 = literal byte, 1 = `u16` with length−`MINLEN` in the top 4 bits and distance
in the low 12, 2 = `u24` with length−`MINLEN` in the top 6 bits and distance in the low 18,
3 = long match: a byte `L`; if `L` = 255, the next byte is distance bits 24..31 and `L` is read
again; if `L` = 254, length−`MINLEN` = `u24`·256 + next byte; otherwise `L` is length−`MINLEN`;
then a `u24` adds distance bits 0..23.

### Bit coder (2)

Bits are read least-significant first from 32-bit little-endian words. A 9-bit symbol: below 256
it is a literal; otherwise `lcode = (sym >> 5) − 8`, `dcode = sym & 31`. Length = `MINLEN` +
base(`lcode`) + extra bits; distance = base(`dcode`) + extra bits. Length extra bits per code:
`0,0,0,1,2,4,8,30`. Distance extra bits per code: `4,4,5,5,5,6,6,7,7,8,8,9,9,10,10,11,11,12,12,13,
13,14,14,15,15,16,17,18,19,21,23,30`. Bases are the running sums, except that the distance codes
are laid out in three bands: codes whose range starts below 512 count in units of 1, the next in
units of 256 until 512·256, the rest in units of 65536 (base = running total in that band's unit,
shifted back; the band switches truncate the total to the new unit).

### Huffman (3) and arithmetic (4) coders

Both code symbols from an alphabet of 842: 0..255 literals; 256 + `dcode`·16 + `lcode` matches
(`dcode` 0..35, `lcode` 0..15); 832 = end-of-block (Huffman only); 833 = "repeat char". Length
extra bits: `0,0,0,0,0,0,0,1,1,2,2,3,3,4,8,30`. A decoded length *l* = base + extra bits maps to:
*l* ≤ 100 → `MINLEN` + *l*; 101..104 → a data table of element size *l*−100 (see below);
above 104 → `MINLEN` + *l* − 4. `dcode` 0..3 select the 1st..4th most recent distance (choosing
the *k*-th moves it to the front; the 1st stays put); `dcode` ≥ 4 is distance code `dcode`−4 of
the table above and the distance is base + extra bits + 1, pushed as most recent. Symbol 833
copies one byte from the most recent distance. The four recent distances start as 0.

**Huffman.** Bits as for the bit coder. The code tree is rebuilt from symbol counters: initially
every counter is 1 and the tree is built (rescale mode 0). After a tree is built, every counter
`c` becomes `c − 1` when 1 < `c` < `F`, else `c − c/F`, with `F` = 2, 3, 4, 6, 8, 10, 12, 16
for rescale modes 0..7. Each decoded symbol increments its counter. Decoding symbol 832 means:
read 3 bits = rescale mode, rebuild the tree, decode again. Tree building: symbols are stable-sorted
by (counter, symbol); two queues, leaves in that order and merged nodes in creation order; each
step merges the two smallest heads, taking two leaves when the second leaf is smaller than the
first merged node, two merged nodes when the first leaf is larger than the second merged node,
else one of each (leaf first); a new node's children get its code plus 0 (first) or plus
`1 << depth` (second) — codes are read least-significant bit first.

**Arithmetic.** A range decoder: `range` = 0xFFFFFFFF and a 32-bit `code` primed with five bytes
(the first is shifted out). Decoding with *b* bits: `range >>= b`, `count = code / range`
(must be below 2^*b*), then after choosing (`cum`, `cnt`): `code −= cum·range`, `range *= cnt`, and
while `range` < 2^24: `range <<= 8`, `code = code << 8 | next byte`. Extra bits ≤ 24 are one step
with `cum` = value, `cnt` = 1; more are 15 bits then the rest, low part first. Symbols use a
14-bit total: counters start at 16384/842 each, the first 16384 mod 842 symbols one more;
frequencies are fixed from the live counters at each rescale, which happens when 16384 symbols
have been counted since the last; at a rescale each live counter `c` becomes `c − 1` for
1 < `c` < 6, else `c − c/6`, and the next rescale comes after `16384 − Σ` more symbols.

### Data tables

A table record (length 101..104) at output position *p* with distance *rows* says the next
*N*·*rows* bytes (*N* = element size) were stored as differences: element *i* > 0 holds
element *i* minus element *i*−1, as 16- or 32-bit little-endian words for *N* = 2 or 4 and with
byte-wise carries for other sizes. Later matches copy the *differenced* bytes; the bytes are
re-added (each element = itself + the previous restored element) only for the output. Tables
never overlap.
