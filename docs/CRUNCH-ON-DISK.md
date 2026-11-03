# CP/M CRUNCH on disk

How `CrunchStream` reads and writes files made by Steven Greenberg's CP/M CRUNCH (1.x and
2.x; extensions with a `Z` in the middle letter, e.g. `.TZT`, `.CZM`).

**Sources.** No specification was published. This description was derived by reading lbrate
1.1 (Russell Marks, GPL-2.0, `readlzw.c`/`readrle.c`) as a description of behaviour and by
running it as a black-box oracle on real files from the Walnut Creek CP/M CD (via
classiccmp.org) and on files we wrote. No code was taken; the implementation follows this page.

## Container

| Bytes | Meaning |
|---|---|
| `76 FE` | magic (`0xFE76` little-endian) |
| name, `00` | original CP/M name; CRUNCH may append a comment after `[` |
| 1 byte | reference revision (real files: `0x12`, `0x23`, `0x24`) |
| 1 byte | version: high nibble `1` = CRUNCH 1.x stream, `2` = 2.x stream |
| 1 byte | check type: `0` = 16-bit additive checksum follows the stream |
| 1 byte | spare (real 2.x files: `0x05`; readers ignore it) |
| codes | LZW codes, most significant bit first, continuous across bytes |
| 2 bytes | when the check type is 0: sum of all output bytes mod 65536, little-endian, at the byte after the last code |

The LZW output is RLE-encoded data: a byte `0x90 n` after byte `c` repeats `c` another `n-1`
times; `0x90 00` is a literal `0x90` and does **not** become the byte to repeat (a following
`0x90 n` still repeats the last non-`0x90` byte).

## 2.x stream

* Codes 0-255 are bytes; 256 ends the stream; 257 resets the table and the code width;
  258 and 259 are filler that readers skip. The first assigned code is 260.
* Codes start 9 bits wide. After each assignment, when the highest assigned code equals
  `2^width - 2` and is below 4094, the width grows by one (at most 12).
* Every code except the first one of the stream and the first one after a reset assigns a new
  code: the previous code's string followed by the first byte of the current one (for a code not
  yet assigned, the previous string followed by its own first byte).
* The table holds 4096 codes. Once it is full, an assignment instead reuses an existing code,
  found through a 5003-slot hash: `h = (((prev >> 4) ^ c) | ((prev & 15) << 8)) + 1`, probing
  `f, f + h - 5003, ...` (wrapping) and taking the first code that has never been read from the
  stream. Codes 0-259 count as read. Every assignment while the table is not full records its
  code in the first free slot of the same probe sequence; the 260 initial entries are recorded
  the same way, with `prev = 0x6FFF` for bytes and `0x7FFF`, `c = 0` for 256-259.

## 1.x stream

* Codes are always 12 bits; code 0 ends the stream; there is no reset.
* Codes are hash-table indices. A string `(prev, c)` is placed at
  `0x800` if it is the start entry for byte 0, otherwise at
  `((b * (b + (a & 1))) >> 4) & 0xFFF` with `a = ((prev + c) | 0x800) & 0x1FFF`, `b = a >> 1`;
  from there the link chain of earlier collisions is followed to its end, and if that slot is
  taken the search tries `+101`, then each next slot (mod 4096), and links the slot found to the
  chain. Index 0 is reserved. The 256 single-byte strings are inserted first, in byte order,
  with `prev = 0xFFFF`. When 4096 entries exist, no more are added.

## What we write

CRUNCH 2.x streams (reference revision `0x20`, version `0x20`, check type 0, spare 0), the
given original name (empty when none is known). Instead of letting the table fill and fall into
code reuse, the encoder emits code 257 as soon as the reader's table is full, so it never relies
on the reuse rule; lbrate accepts these resets. Runs of three or more equal bytes (other than
`0x90`) are RLE-coded.
