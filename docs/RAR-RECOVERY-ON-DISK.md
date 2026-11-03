# RAR recovery records — on-disk notes

RARLAB does not publish the recovery record's layout, and unrar's source is licence-restricted
(it forbids recreating RAR compression and carries no licence for anything else). Nothing below
comes from it. Everything was **measured from rar's own output**. WinRAR 7.12 (`Rar.exe`, x64,
trial) wrote the RAR 5 records and WinRAR 6.24 (`Rar.exe -ma4`) wrote the RAR 4 ones. Python
probes then read those archives back:

- **Parameters by sweep.** Archives were written with stored random data at many sizes and
  percentages, then the headers were read.
- **Byte-to-unit mapping by differential runs.** One archive was copied, a single byte was
  flipped, `rar rr` was rerun, and the two records were compared. Bytes that move together share a
  unit. The size of the change gives a coefficient.
- **Field and coefficients by algebra.** The coefficients were solved over GF(2^16). The 16-bit
  field was found by checking Cauchy consistency of 195 measured coefficients, and the RS form by
  brute force over the 32 GF(2^8) polynomials.
- **Hypotheses checked against whole records.** Every word of every recovery shard of five rar
  archives was reproduced.
- **Checksums by their linear part.** Check values that are affine in the data were pinned to
  CRCs. Where only the constant was unknown, init and xorout were solved over GF(2) from four
  record lengths (128 equations, rank 64, no contradiction).

The result is proven both ways in `Compression.Tests/Rar/RarRecoveryTests.cs`:

- rar 7.12 `r` rebuilds damaged RAR 5 archives that `RarWriter` protected, byte for byte, and
  rar 6.24 `r` does the same for RAR 4 archives from `Rar4Writer`. `rar t` reports the record
  "OK".
- `RarRecovery.Repair` rebuilds damaged archives that rar protected (`-rr5%`, `-rr20%`, `-ma4`),
  byte for byte.

## RAR 5 recovery record

A service header named `RR` sits after the last entry (header flags 7: extra area, data area,
skip if unknown). Its extra area holds one record of type 7 (service data) whose value is the
percentage. Its data area is the record. The main header sets archive flag 8 ("recovery record
present"). rar also adds a locator record (type 1, flag 2) with the record's offset counted from
the main header. The locator is optional. `RarWriter` omits it, and rar finds the record without
it.

**Protected data.** The protected data is file bytes `[0, P)`, where `P` is the offset of the
`RR` service header. Those bytes are cut into `ND` data shards of `S` bytes, and the last shard is
zero-padded.

| Parameter | rar 7.12 chooses (measured over 10 sizes × 3 percentages) |
|---|---|
| `ND` | `min(200, ceil(P / 1024))` |
| `S` | `ceil(P / ND)`, rounded up to even |
| `NR` (recovery shards) | `max(1, floor(ND × percent / 100))` |

**Code.** The code is Reed-Solomon over GF(2^16), polynomial `x^16 + x^12 + x^3 + x + 1`
(`0x1100B`). Shards are read as little-endian 16-bit words. Recovery shard `I`, word `w`, is
`Σ_J D_J[w] / ((ND + I) xor J)`, a Cauchy matrix with rows `ND..ND+NR-1` and columns `0..ND-1`.

**Data area.** The data area is `NR` blocks of `64 + 8 × (ND + 1) + S` bytes, one per recovery
shard:

| Offset | Size | Field |
|---|---|---|
| 0 | 4 | `{RB}` |
| 4 | 8 | CRC-64/XZ (ECMA-182, reflected, init and xorout all ones) of bytes 12 to the end of the block |
| 12 | 4 | block size |
| 16 | 4 | `64 + 8 × (ND + 1)`: header plus table |
| 20 | 2 | `01 01` (constant in every archive measured) |
| 22 | 8 | zero (constant in every archive measured) |
| 30 | 4 | length of the last data shard, `P - (ND - 1) × S` |
| 34 | 8 | `P` |
| 42 | 8 | `S` |
| 50 | 8 | block size again |
| 58 | 2 | `ND` |
| 60 | 2 | `NR` |
| 62 | 2 | this block's recovery shard index |
| 64 | 8 × ND | CRC-64 of each data shard: the ECMA-182 register from 0, no final xor, over the shard's real bytes (not the padding) |
| 64 + 8 × ND | 8 | random: two runs over the same archive give different values; every block of one record carries the same one |
| 64 + 8 × (ND + 1) | S | recovery shard |

**Repair.** `RarRecovery` finds blocks by `{RB}` and their CRC-64. Damaged blocks are skipped,
and their positions follow from the good ones. Damaged data shards are those whose CRC-64
mismatches. For `E` damaged shards and at least `E` good blocks, it solves the `E×E` Cauchy
sub-system and checks the rebuilt shards against the table. It then recomputes any damaged
recovery block. When fixing, rar rewrites the end-of-archive header with header flags 4.
`RarWriter` writes it that way, so a repaired archive matches the original byte for byte.

## RAR 2.x-4.x recovery record

A `NEWSUB` block (type `0x7A`) named `RR` sits after the last entry. The main header sets
`MHD_PROTECT` (`0x40`).

| Offset | Size | Field |
|---|---|---|
| 0 | 2 | low 16 bits of the CRC-32 of header bytes 2..53 |
| 2 | 1 | `0x7A` |
| 3 | 2 | flags `0xC000` |
| 5 | 2 | header size, 54 |
| 7 | 4 | data size (`PACK_SIZE`) |
| 11 | 4 | data size again (`UNP_SIZE`) |
| 15 | 1 | host OS (2) |
| 16 | 4 | CRC-32 of the data area with the register **starting at `0x0FFFFFFF`** (final value inverted). Solved over GF(2) from four data lengths; it equals `zlib.crc32(data, 0xF0000000)`. |
| 20 | 4 | DOS time |
| 24 | 1 | 29 |
| 25 | 1 | `0x30` (stored) |
| 26 | 2 | name size, 2 |
| 28 | 4 | attributes, 0 |
| 32 | 2 | `RR` |
| 34 | 8 | `Protect+` |
| 42 | 4 | `R`, parity sectors |
| 46 | 8 | `N`, data sectors: `ceil(P / 512)` for `P` the block's offset |

**Data area.** The data area is `N` 16-bit sector checks, then `R` parity sectors of 512 bytes.
A sector check is the low half of the CRC-32 register after the zero-padded sector, *not*
inverted. Parity sector `k` is the XOR of every data sector `i` with `i mod R = k`. A contiguous
run of up to `R` damaged sectors therefore hits each parity sector at most once and rebuilds;
two damaged sectors in one parity group do not.

rar's choice of `R` from a percentage could not be pinned: 129 sectors gave 7 at 5% and 14 at
10%, and 391 sectors gave 12 at 3%. `Rar4Writer` uses `ceil(N × percent / 100)`. rar repairs
from any `R`, as the round trip above shows.

## Not covered

- **Shifting damage** (bytes inserted or lost). rar reports such blocks as "relocated". Repair
  here is in place only.
- **Damage outside the protected range.** This covers the record's own header and the
  end-of-archive header. For RAR 4 it also covers the record's data, whose CRC-32 must hold
  before it is trusted.
- **Writing a record into a volume set.** `RarWriter` and `Rar4Writer` refuse it. Each rar
  volume carries its own record, and repair works on such volumes one file at a time.
