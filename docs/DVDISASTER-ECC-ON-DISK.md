# dvdisaster RS01 error-correction files (`.ecc`)

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
