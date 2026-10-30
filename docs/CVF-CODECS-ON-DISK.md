# CVF cluster codecs on disk

The per-cluster compression formats of the DOS compressed-volume family: Microsoft
DoubleSpace / DriveSpace (DS-0-x, JM-0-x), DriveSpace 3 (JM-0-1 "High", SQ-0-0 "Ultra") and
Stac Stacker (SD-3, SD-4). `Compression.Core.FileSystems.Cvf` (`CvfLzCodec`, `Sd3Codec`,
`Sd4Codec`) implements this document — it was written from this document and the reference vectors
alone, with dmsdos run only as a black box; the container layouts (MDFAT, AMAP, BitFAT) live in the
`FORMAT-NOTES.md` beside each file-system reader.

## 📚 Provenance

| Source | Rung | Used for |
|--------|------|----------|
| dmsdos (Frank Gockel, Pavel Pisa, Jaroslav Fojtik; GPL), <https://github.com/sandsmark/dmsdos>, master as of 2026-10 | 2 — licence-incompatible | Read as a description of behaviour to write this document; its library is the **black-box oracle** that produced the reference vectors and that reads our output back. No code was taken. |
| RFC 1974, *PPP Stac LZS Compression Protocol* (and RFC 2395) | 3 — standard | The LZS token grammar SD-3 uses. |
| dmsdos `doc/dmsdos.doc` | 4 — project documentation | Method names: JM-0-0 *Normal*, JM-0-1 *High*, SQ-0-0 *Ultra* for DriveSpace 3; SD-3 / SD-4 for Stacker 3 / 4. |

The constants in the tables below (offset classes, length extensions, run tokens) are copied
exactly; they are arbitrary-but-agreed values.

No Microsoft or Stac binary was available, so "genuine" here means *as written and read by
dmsdos*, which in turn was built against real DOS and Windows 95 volumes. That is the strongest
evidence obtainable on this machine, and it is stated as such in every test that relies on it.

## 🧩 Where a method is recorded

| Volume | Per-cluster size info | Method selector |
|--------|----------------------|-----------------|
| DoubleSpace / DriveSpace (MSDBL6.0) | MDFAT: on-disk sectors, decoded sectors, *stored* and *used* flags | First 4 payload bytes: 16-bit magic + 16-bit version |
| DriveSpace 3 | MDFAT (5-byte entries), same fields | Same 4-byte header |
| Stacker | AMAP: on-disk sectors and a 3-bit flag field; the decoded size is always one cluster | First little-endian **word** only: `0x0081` → SD-4, `0x5344` ("DS") → DS decoder, anything else → SD-3 |

An SD-3 stream can never start with either word: its first token must be a literal (there is
nothing to copy from yet), so its first bit is 0 and its first byte is below `0x80`, ruling out
`81 00`; and for `44 53` the second token would be a long-offset match reaching before the start.

### Stacker AMAP flags

Bits 7 and 5 of the AMAP entry's third byte (bit 6 marks a deleted cluster and does not change
its kind), together with whether the run fills a whole cluster:

| Bit 7 | Bit 5 | Run shorter than a cluster | Run a whole cluster |
|---|---|---|---|
| 0 | 0 | compressed | stored |
| 0 | 1 | stored, short | stored |
| 1 | 0 | **fragmented** (first sector starts `0xED`, piece count, 4-byte piece list) | stored (directory) |
| 1 | 1 | **suballocated** (signature `0x1234` at the sector end) | suballocated |

A compressed cluster can therefore never occupy all of its cluster's sectors: a run that long reads
as stored whatever its content (the table above). A writer whose
compressed payload needs every sector stores the cluster raw instead.

### Stacker fragmented clusters

Measured with dmsdos as a black box: volumes were built with candidate layouts and kept only where
`dcread` returned the cluster byte-exact (no Stac software or sample volume was available, so this is
"as dmsdos reads it"; `StackerFragmentedClusterTests` keeps the comparison).

* The AMAP entry (bit 7 set, bit 5 clear) names the **first piece**; its sector count covers that
  piece only, header sector included. An AMAP count covering all pieces is rejected.
* The first piece's first sector begins with a header:

  | Offset | Size | Field |
  |---|---|---|
  | 0 | 1 | `0xED` |
  | 1 | 1 | *n*, the number of further pieces |
  | 2 | 2 | little-endian: the further pieces' sector counts summed, minus one (dmsdos refuses the cluster when this disagrees with the list) |
  | 4 | 4·*n* | one entry per further piece: 16-bit little-endian sector, sector count − 1, one byte that was 0 in every accepted volume |

* The compressed payload (chosen by its first word as for any Stacker cluster; SD-3 and SD-4 were
  verified, a DS payload was not read by dmsdos in this position) starts right after the entries, at byte 4 + 4·*n* of the first piece, and continues through the
  pieces in list order. A stored payload is not possible: dmsdos decompresses fragmented clusters.
* Accepted: one to three further pieces, first pieces of one and two sectors, SD-3 and SD-4 payloads.
  Not established: the meaning of the entries' fourth byte (perhaps the sector's high bits on volumes
  past 65535 sectors) — `GenuineStackerReader` refuses an entry where it is non-zero.

Suballocated clusters (bit 7 and bit 5 set) are refused by `GenuineStackerReader`; their layout has
not been measured.

## 🔢 DS-0-x and JM-0-x

Bits are taken **LSB-first** from consecutive bytes (equivalently, from little-endian 16-bit
words). The payload starts with the 4-byte method header `44 53 00 x` ("DS", DS-0-*x*, *x* = 0..2)
or `4A 4D 00 x` ("JM", JM-0-*x*, *x* = 0..1; JM-0-1 is DriveSpace 3 *High*). The version byte *x*
does not change the bitstream. Below, "bits" are listed in the order they are read.

**DS tokens**

| First bits | Then | Meaning |
|---|---|---|
| `0 0` | 6-bit *d* | match, distance *d* (1..63) |
| `1 0` | 7-bit *v* | literal `0x80 | v` |
| `0 1` | 7-bit *v* | literal *v* |
| `1 1 0` | 8-bit *v* | match, distance 64 + *v* |
| `1 1 1` | 12-bit *v* | match, distance 320 + *v* |

**JM tokens**

| First bits | Then | Meaning |
|---|---|---|
| `0` | 7-bit *v* | literal *v* |
| `1 1` | 7-bit *v* | literal `0x80 | v` |
| `1 0` | distance code, length code | match |

JM distance code: `0` + 6 bits (*d*), `1 0` + 8 bits (64 + *v*), `1 1` + 12 bits (320 + *v*).

**Length code** (both): *z* zero bits, a one bit, then *z* bits *v* (0 ≤ *z* ≤ 8); *L* = 2 + 2^*z*
+ *v*, so 3..513. Nine zero bits are invalid. A JM match copies *L* bytes, a DS match *L* − 1.

**Sync.** Distance 4415 (`0x113F`, the all-ones 12-bit long form) is a sync marker with no length:
it may appear where the decoded position is a multiple of 512, and one must follow the last byte
of the cluster (DS: `1 1 1` + 12 ones; JM: `1 0` + `1 1` + 12 ones). Stacker's use of the DS
decoder does not require the final sync.

Copies may overlap (distance shorter than length) and are performed byte by byte.

**Measured: one sync per sector.** In all 16 DS/JM reference streams (8 KiB and 32 KiB clusters)
the dmsdos compressors emit a sync after every 512 decoded bytes, not only after the last one, and
no match runs across a 512-byte boundary (0 of all matches) — distances do reach back across
boundaries (up to 4414). Our encoder writes the same shape. The decoder accepts syncs anywhere.

**A cluster shorter than its nominal size** (a file's last cluster, MDFAT decoded size below the
cluster) ends with a sync followed by nothing but zero padding; decoding stops there and the rest of
the cluster reads as zero. A sync with non-zero bits behind it is a mid-stream marker and decoding
continues. Stacker's use of the DS grammar also ends where the stream runs out or reaches its zero
padding (a `0 0` token with distance 0).

## 🔢 SQ-0-0

Bytes `53 51` ("SQ"), a 16-bit version (0), then a raw **RFC 1951 DEFLATE** stream: the same block
types, fixed code, code-length alphabet order, length/distance base and extra-bit tables and LSB-first
packing. The decoder stops at the final block or when the cluster is full; block type 3, more than
286 literal/length codes or more than 30 distance codes are invalid.

## 🔢 SD-3

No header. A Stac **LZS** token stream (RFC 1974), bits taken **MSB-first** from consecutive bytes:

| Bits | Meaning |
|---|---|
| `0` + 8 bits | literal |
| `1 1` + 7 bits *d* (1..127) | match, distance *d*, then a length code |
| `1 0` + 11 bits *d* (128..2047) | match, distance *d*, then a length code |
| `1 1 0000000` | end marker |

Length code: `00` 2, `01` 3, `10` 4, `11 00` 5, `11 01` 6, `11 10` 7, `11 11` then 4-bit groups
added to 8, continuing while a group is `1111`.

After the end marker the stream is zero-padded to a byte boundary and followed by one **check
byte**: `0xFF` XOR every decoded byte. The check is applied whenever the payload extends past the
token stream (it always does on disk, payloads being whole sectors). The decoded data may be
shorter than the cluster; the remainder reads as zero.

## 🔢 SD-4

Header word `0x0081`. Bits are taken **MSB-first from little-endian 16-bit words** (the high bit
of the word formed by bytes *b0*, *b1* as `b0 | b1 << 8` comes first).

**Prefix codes.** Both codes are canonical: given a code length per symbol (0 = unused, at most
15), codes are assigned in order of increasing length and, within a length, increasing symbol
number, each code being the previous plus one, shifted left on every length step. Codes are read
MSB-first. The code must be complete — the lengths' Kraft sum is exactly one — or the stream is
invalid.

**Header.**

| Field | Bits |
|---|---|
| *first* − 1 | 3 |
| *last* (the longest main-code length; *first* ≤ *last* ≤ 15) | 5 |
| helper length of symbol 0 | 4 |
| helper lengths of symbols *first*..*last* | 4 each |
| helper lengths of symbols 16..21 | 4 each |

Helper symbols 1..*first*−1 and *last*+1..15 have length 0.

**Main code lengths.** 336 (`0x150`) lengths are read with the helper code. Helper symbols 0..15 are
a length; 16..21 are run tokens:

| Helper symbol | Run |
|---|---|
| 16 | two zeros |
| 17 | three zeros |
| 18 | zeros, 4 + 3-bit *v*; when *v* = 7, add 7-bit groups while a group reads `0x7F` |
| 19 | the previous length twice |
| 20 | the previous length three times |
| 21 | the previous length, 4 + the sum of 3-bit groups, continuing while a group reads 7 |

Runs are clipped at the end of the table.

**Data.** Main symbols 0..255 are literals. Symbol 255 + *t* is token *t*:

*Short match*, *t* = 1..63: class *c* = (*t* − 1) / 3, length 3 + (*t* − 1) mod 3, distance
`base[c]` + `bits[c]` extra bits:

| *c* | 0 | 1 | 2 | 3 | 4 | 5 | 6 | 7 | 8 | 9 | 10 | 11 | 12 | 13 | 14 | 15 | 16 | 17 | 18 | 19 | 20 |
|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|
| base | 1 | 2 | 3 | 4 | 6 | 8 | 12 | 16 | 24 | 32 | 48 | 64 | 96 | 128 | 192 | 256 | 384 | 512 | 768 | 1024 | 1536 |
| bits | 0 | 0 | 0 | 1 | 1 | 2 | 2 | 3 | 3 | 4 | 4 | 5 | 5 | 6 | 6 | 7 | 7 | 8 | 8 | 9 | 9 |

*Long match*, *t* = 64..79: a 2-bit selector *s*, then the distance `add[s]` + `len[s]` bits with
`len` = {5, 7, 9, 11} and `add` = {1, 33, 161, 673}; then the length *t* − 58 (6..21). A length of 21
is extended by 4 bits; if the sum is 36, by 8 more; at 291 by 12 more; at 4386 by 16 more.

*End*, *t* = 80. Decoding also stops when the cluster is full (the end token then follows; it is
not needed to recover the data). A match reaching before the cluster start is invalid; a final
match longer than the space left is clipped.

**Our encoder** emits short and long matches (all four length extensions), literals and the end
token; it transmits the code lengths with all six run tokens and builds both codes with package-merge
(optimal, length-limited, always complete). Measured: dmsdos reads main codes 15 bits deep
(`GivenSd4CodesOf15Bits_WhenReadByDmsdos_ThenTheClusterIsByteExact`), so the encoder uses the format's
full 15-bit limit for both codes.

## ✅ Verification

* `Compression.Tests/Cvf/ReferenceVectors/` — 32 cluster payloads written by the dmsdos
  compressors (DS-0-0, JM-0-0, JM-0-1, SQ-0-0, SD-3, SD-4 over text, x86 code, a record table, a
  small-alphabet stream and a FAT directory, plus 32 KiB DriveSpace 3 clusters for JM and SQ);
  `CvfReferenceVectorTests` decodes every one to its input. The README there says how they were
  made.
* `GenuineStackerDmsdosTests`, `DriveSpace3GenuineDmsdosTests` and the DoubleSpace driver gate in
  `ExternalFsInteropTests` have dmsdos mount volumes our writers produced — Stacker with SD-3, SD-4,
  DS and Auto clusters among them — and read every file back byte-exact.

## ⚠️ Not covered

* **DriveSpace 3 fragmented clusters** (an MDFAT flag dmsdos itself describes as guessed) and
  **Stacker suballocated clusters** are not read: no layout has been measured for either.
* No Microsoft or Stac tool has produced or read any of these streams on this machine.
