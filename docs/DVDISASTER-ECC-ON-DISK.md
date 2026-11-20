# dvdisaster error-correction files (`.ecc`): RS01 and RS03

What an RS01 `.ecc` file is, as the writer in `FileFormat.DvdisasterEcc` implements it. Derived
from dvdisaster (GPL-3.0, read for behaviour only; sourcing rung 2) and confirmed against files
dvdisaster 0.79.10 writes for images of our choosing: every field below was compared byte for byte.

## Image geometry

The image is split into 2048-byte sectors: `sectors = ceil(size / 2048)`; `inLast` is the number
of real bytes in the last sector (2048 when the size is a multiple of 2048). For the parity and
the CRCs the last sector is padded with zero bytes; sector numbers at or past `sectors` read as
2048 zero bytes.

## Layout

| Offset | Size | Contents |
| ---: | ---: | --- |
| 0 | 4096 | header |
| 4096 | 4 × sectors | one CRC per sector, little-endian |
| 4096 + 4 × sectors | nroots × 2048 × L | parity |

## Header (little-endian, 4096 bytes, unused bytes zero)

| Offset | Size | Field |
| ---: | ---: | --- |
| 0 | 12 | `*dvdisaster*` |
| 12 | 4 | `RS01` |
| 16 | 4 | method flags: byte 0 is 1; byte 3 is a release flag (0 for a release, 1 for a development build) |
| 20 | 16 | MD5 of sector 16 (the fingerprint sector), padded as above; 16 zero bytes when the image has 16 sectors or fewer |
| 36 | 16 | MD5 of the image (its real bytes) |
| 52 | 16 | MD5 of everything after the header (CRCs and parity) |
| 68 | 8 | sectors |
| 76 | 4 | data bytes per codeword, `ndata = 255 - nroots` |
| 80 | 4 | parity bytes per codeword, `nroots` (8 to 100) |
| 84 | 4 | creator version (dvdisaster writes e.g. 7910 for 0.79.10) |
| 88 | 4 | needed version: 5500, or 6600 when `inLast` is not 2048 |
| 92 | 4 | fingerprint sector, 16 |
| 96 | 4 | self CRC, 0 for RS01 |
| 116 | 4 | `inLast` |

## Sector CRCs

CRC-32 with the reflected polynomial 0x04C11DB7 and initial value 0xFFFFFFFF, without the final
inversion (that is, the usual CRC-32 of the sector, inverted), over the 2048 bytes of each sector.

## Parity

Reed-Solomon over GF(2^8) with field polynomial 0x187 (x^8 + x^7 + x^2 + x + 1), first
consecutive root 112 and primitive element 11 (the CCSDS choices): the generator polynomial is
the product of (x − α^(11·(112 + i))) for i = 0 … nroots − 1.

The image is cut into `ndata` layers of `L = ceil(sectors / ndata)` sectors; layer k holds
sectors `k·L … k·L + L − 1`. For every sector offset `j < L` and byte position `b < 2048` the
codeword is byte b of sector `k·L + j` for k = 0 … ndata − 1, in that order, followed by its
`nroots` parity bytes as the systematic encoder leaves them in its shift register (the
coefficient of the highest power first). The parity section holds these nroots-byte groups for
j = 0 … L − 1, b = 0 … 2047, one after the other.

# RS03 error-correction files

What an RS03 `.ecc` file is, as `DvdisasterEccWriter.CreateRs03` writes it. It has the same
sourcing as RS01 above. Every byte was compared with files dvdisaster 0.79.10 wrote with
`-mRS03 -o file`: one image of 40 sectors, one of 1000 sectors plus 333 bytes, and one of 10
sectors (shorter than the fingerprint sector). RS03 can also augment an image instead of writing a
separate file. That form is not covered here.

## Geometry

- **Codewords.** `nroots` is 8 to 170 and `ndata = 255 − nroots`. A codeword holds `ndata − 1`
  data bytes, one CRC-layer byte and `nroots` parity bytes.
- **Layers.** The image is cut into `ndata − 1` data layers of
  `L = floor((sectors + ndata − 2) / (ndata − 1))` sectors each. Data layer k holds image sectors
  `k·L … k·L + L − 1`.
- **Padding.** A data-layer sector at or past `sectors` is a padding sector (below). The last real
  sector is zero-padded, as for RS01.

## Layout

| Offset | Size | Contents |
| ---: | ---: | --- |
| 0 | 4096 | header |
| 4096 | 2048 × L | CRC layer: one CRC block per sector offset |
| 4096 + 2048 × L | 2048 × L × nroots | parity layers: layer i, then i + 1 |

## Header (little-endian, 4096 bytes, unused bytes zero)

The fields RS01 uses keep their offsets.

| Offset | Size | Field |
| ---: | ---: | --- |
| 0 | 16 | `*dvdisaster*RS03` |
| 16 | 4 | method flags: byte 0 is 2 (an ecc file; bit 0 would announce an image MD5, which is not written on creation); byte 3 is the release flag |
| 20 | 16 | fingerprint: MD5 of sector 16, or 16 zero bytes when the image has 16 sectors or fewer |
| 36 | 16 | zero (no image MD5) |
| 52 | 16 | zero (RS03 has no MD5 of the ecc data) |
| 68 | 8 | sectors |
| 76 | 4 | `ndata` |
| 80 | 4 | `nroots` |
| 84 | 4 | creator version |
| 88 | 4 | needed version, 7900 |
| 92 | 4 | fingerprint sector, 16 |
| 96 | 4 | self CRC (below) |
| 116 | 4 | `inLast` |
| 120 | 8 | `L`, sectors per layer |

## Self CRCs

The header (4096 bytes) and each CRC block (2048 bytes) carry a CRC of themselves. To compute
it, set the field to 0x004C5047 and take the sector CRC (RS01 section) over the whole structure.

## CRC layer

CRC block j (2048 bytes, little-endian, unused bytes zero) holds, at offset 4·k for each data layer
k, the sector CRC of that layer's sector j + 1. For the last block, j = L − 1, it is sector 0. Then:

| Offset | Size | Field |
| ---: | ---: | --- |
| 1024 | 16 | `*dvdisaster*RS03` |
| 1040 | 4 | method flags, as in the header |
| 1044 | 4 | creator version |
| 1048 | 4 | needed version, 7900 |
| 1052 | 4 | fingerprint sector, 16 |
| 1056 | 16 | fingerprint |
| 1072 | 16 | zero (no image MD5) |
| 1088 | 8 | sectors |
| 1096 | 4 | `inLast` |
| 1100 | 4 | `ndata` |
| 1104 | 4 | `nroots` |
| 1112 | 8 | `L` |
| 1120 | 4 | self CRC |

## Padding sectors

A padding sector is 2048 bytes, zero except for these NUL-terminated ASCII strings:

| Offset | Text |
| ---: | --- |
| 0 | `dvdisaster padding sector       This is a padding sector needed for augmenting the image with error correction data.` |
| 0x100 | `Padding sector marker version` |
| 0x120 | `1.00` |
| 0x140 | `Padding sector number` |
| 0x160 | the sector number in decimal |
| 0x180 | `Medium fingerprint` |
| 0x1C0 | `Medium fingerprint sector` |
| 0x1E0 | `16` |

In addition, the 16 fingerprint bytes sit at 0x1A0, and `dvdisaster padding sector end marker`
occupies bytes 2011–2046.

## Parity

The Reed-Solomon code is the same as RS01's: the same field, generator and register order.
- **Codeword.** For sector offset `j < L` and byte `b < 2048`, the codeword is byte b of sector j
  of data layers 0 … ndata − 2, then byte b of CRC block j, followed by the `nroots` register
  bytes.
- **Storage.** Parity layer i stores register byte i of every codeword: sector j of layer i holds
  it for `b = 0 … 2047`.
