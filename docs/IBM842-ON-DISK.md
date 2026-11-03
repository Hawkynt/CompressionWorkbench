# IBM 842 on disk

How `BB_842` reads and writes the 842 format of IBM's POWER NX accelerator, as produced and
consumed by the Linux kernel's software implementation (`lib/842`, used by zswap and the
`842` crypto driver).

**Sources.** The format is described in the comment that opens `lib/842/842.h` (Linux,
GPL-2.0) and its operation table is the `decomp_ops` table in `842_decompress.c`; both were read
as a description, and `sw842_compress`/`sw842_decompress` were compiled in user space and run as
a black-box oracle. The table below is copied exactly; the implementation follows this page.

## Stream

A big-endian bit stream (most significant bit first). It is a sequence of 5-bit operation codes,
each followed by its arguments, ended by the end code, then a 32-bit CRC, then zero padding:

| Code | Meaning | Arguments |
|---|---|---|
| `0x00`-`0x19` | template: produce 8 bytes | up to four actions from the table below |
| `0x1B` | repeat the last 8 output bytes `N + 1` times | `N`, 6 bits |
| `0x1C` | 8 zero bytes | none |
| `0x1D` | short data (software only): `N` literal bytes, 1-7 | `N`, 3 bits, then `N` bytes |
| `0x1E` | end | none |

After the end code: CRC-32 of all output bytes (polynomial `0x04C11DB7`, not reflected,
initial value 0, no final XOR), 32 bits. The writer then completes the last byte with zero bits
and pads the stream with zero bytes to a multiple of 8 bytes; readers ignore everything after
the CRC. The software compressor writes short data only for the last `len mod 8` bytes.

## Templates

Each action produces 2, 4 or 8 bytes, together 8. `Dn` is `n` literal bytes in the stream;
`In` is an index of 8 (I2), 9 (I4) or 8 (I8) bits.

| Code | Actions | Code | Actions |
|---|---|---|---|
| `0x00` | D8 | `0x0D` | I2 D2 I2 I2 |
| `0x01` | D4 D2 I2 | `0x0E` | I2 D2 I4 |
| `0x02` | D4 I2 D2 | `0x0F` | I2 I2 D4 |
| `0x03` | D4 I2 I2 | `0x10` | I2 I2 D2 I2 |
| `0x04` | D4 I4 | `0x11` | I2 I2 I2 D2 |
| `0x05` | D2 I2 D4 | `0x12` | I2 I2 I2 I2 |
| `0x06` | D2 I2 D2 I2 | `0x13` | I2 I2 I4 |
| `0x07` | D2 I2 I2 D2 | `0x14` | I4 D4 |
| `0x08` | D2 I2 I2 I2 | `0x15` | I4 D2 I2 |
| `0x09` | D2 I2 I4 | `0x16` | I4 I2 D2 |
| `0x0A` | I2 D2 D4 | `0x17` | I4 I2 I2 |
| `0x0B` | I2 D4 I2 | `0x18` | I4 I4 |
| `0x0C` | I2 D2 I2 D2 | `0x19` | I8 |

## Indexes

An `In` index `k` names the `n`-byte granule at offset `k * n` of a ring of `2^bits * n` bytes
(512 for I2, 2048 for I4 and I8) laid over the output. With `t` the output length so far rounded
down to a multiple of 8, the granule is the most recent one at that ring offset that starts
before `t`: offset `k * n` in the ring section that contains `t`, or in the section before it
when that offset is at or past `t`'s position in its section. The granule must end at or before
`t`.

## What we write

Whole 8-byte groups: a repeat when the group equals the one before it (up to 64 at a time),
8 zero bytes as the zeros code, otherwise the cheapest template whose index actions all find the
granule in the ring (the most recent equal granule still inside it), else `D8`. Then short data
for the remainder, the end code, the CRC and the padding.
