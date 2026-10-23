# bzip 0.21 (`.bz`) on disk

Julian Seward's bzip 0.21 (August 1996) is the arithmetic-coded predecessor of bzip2;
bzip2 cannot read it. Its source is GPL-2.0, so this repository implements the format
from this description, written by reading the program (sourcing rung 2), and checks the
result against the program itself, built from the FreeBSD distfile
`bzip-0.21.tar.gz` (SHA-256 `85846f55406fa019305cee6f0aa3637769e857d8fff00eba26d8effc4539b014`).

## Bit stream

All bits are written most-significant first into bytes. The file is:

1. Four bytes written as plain bits: `B`, `Z`, `0`, and `'0' + n` where `n` (1–9) is the
   block size in units of 100 000 RLE symbols.
2. Everything else goes through one arithmetic coder that runs from the first block to
   the end of the file.
3. After the last bit of the coder's termination the final byte is padded with zero bits.

## Arithmetic coder

A carry-free binary-renormalising range coder in the style of Moffat, Neal and Witten
("Arithmetic Coding Revisited") with `b = 26` code bits.

State: low `L` (encoder), code value `D` (decoder), range `R`; start `L = 0`,
`R = 2^25`; the decoder starts by reading 26 bits into `D`.

To code a symbol with cumulative frequency range `[lo, hi)` out of total `T`:
`r = R / T`; the low end moves by `r · lo` (`L += r·lo` / `D -= r·lo`); the new range
is `r · (hi − lo)`, except for the last symbol of the alphabet (`hi == T`), which takes
the rest: `R − r · lo`. The decoder finds the symbol from `min(T − 1, D / r)`.

Renormalisation while `R ≤ 2^24`: the encoder emits `0` if `L + R ≤ 2^25`, emits `1`
and subtracts `2^25` if `L ≥ 2^25`, and otherwise counts one pending opposite bit and
subtracts `2^24`; then doubles `L` and `R`. An emitted bit is followed by all pending
bits, inverted. The decoder doubles `R` and shifts the next stream bit into `D`.
Termination emits the 26 bits of `L`, most significant first, each with its pending bits.

## Adaptive frequency models

A model over symbols `1..N` starts with every frequency equal to the increment `inc`
(or 1, for a static model with `inc = 0`). After a symbol is coded its frequency and the
total grow by `inc`; when the total exceeds `limit`, every frequency becomes
`(f + 1) / 2` (integer) and the total is recomputed.

| Model | Symbols | inc | limit | Use |
|---|---|---|---|---|
| bytes | 256 | 0 | 256 | plain bytes: symbol = byte + 1 |
| basis | 11 | 12 | 1000 | MTF event class |
| 2–3 | 2 | 4 | 1000 | value − 1 (offset in class) + 1 |
| 4–7 | 4 | 3 | 1000 | |
| 8–15 | 8 | 3 | 1000 | |
| 16–31 | 16 | 3 | 1000 | |
| 32–63 | 32 | 3 | 1000 | |
| 64–127 | 64 | 2 | 1000 | |
| 128–255 | 128 | 1 | 1000 | |

The byte model persists for the whole file; the other eight are reset at the start of
every block.

## Block

1. A 32-bit big-endian number through the byte model: `origPtr + 1`, negated for the
   last block of the file.
2. Move-to-front symbols, coded as a basis class, plus (for classes 4–10) a symbol in
   the class's sub-model giving the offset inside the class:

   | Basis symbol | Meaning |
   |---|---|
   | 1 | RUNA |
   | 2 | RUNB |
   | 3 | MTF value 1 |
   | 4 … 10 | MTF value in 2–3, 4–7, 8–15, 16–31, 32–63, 64–127, 128–255; the sub-model symbol `s` gives `low + s − 1` |
   | 11 | end of block |

   A maximal sequence of RUNA/RUNB symbols is a run of MTF value 0 whose length is
   written in bijective base 2, most significant digit first: RUNA is digit 2, RUNB digit 1
   (`n = 2n + 2` for RUNA, `n = 2n + 1` for RUNB). The MTF list starts as `0, 1, …, 255`.
3. The decoded MTF output is the last column of the sorted rotation matrix of the
   block; `origPtr` is the row holding the unrotated block. Inverting it gives the block.
4. "Spotting": starting at position 8000 and while the position is below the block's
   last index, the byte at that position was incremented (mod 256) before sorting and is
   decremented after; the step pattern cycles through deltas `1 → 4 → 5 → 9 → 2 → 6 → 7 → 3 → 1`,
   moving `8000 + 17 · (newDelta − 5)` each time (the delta first used is the successor of
   the current one, starting from 1).
5. The block is run-length coded: a run of 1–3 equal bytes is literal; a run of 4–255
   is four copies followed by a byte holding `run − 4`; longer runs are split at 255. The
   decoder resets its run counter at each block start and after every count byte.
6. The last block of the file ends with an extra byte `42` that is not output.

A block holds at most `100000 · n − 20` RLE symbols before a new one starts; runs are
never split across blocks.

## Trailer

A 32-bit big-endian CRC through the byte model: the bzip2 CRC-32 (polynomial
`0x04C11DB7`, MSB first, initial `0xFFFFFFFF`, final complement) over all output bytes
of the file, followed by the coder termination.
