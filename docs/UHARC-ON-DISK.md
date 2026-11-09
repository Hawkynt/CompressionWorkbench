# UHARC — on-disk notes

UHARC is Uwe Herklotz's closed-source archiver (1997–2005). No specification was ever
published and no implementation exists under a licence this repository can take, so this
document is the specification `FileFormat.Uharc` is implemented from. It covers the 0.6 format
(0.6, 0.6a and 0.6b write the same archives; the documentation says so and the header carries
version byte 6) and, in its own section, the 0.2 format, as far as they were established; what
is not covered is listed at the end.

**Status: read, write-once.** Container, ciphers, every 0.6 mode — store, RLE, LZ78, LZP, PPM and ALZ
with its nine multimedia predictors —, the executable and multimedia filters, passwords and SFX
stubs are established and verified, and so is UHARC 0.2 (see its section). Archives of 0.3 and
0.4 are not, and are refused. The writer produces stored and LZP-packed 0.6 archives (see
Writing).

## Evidence

Everything below was measured with **UHARC 0.6b** (`uharc06b.zip`, archive.org item `uharc06b`,
MD5 `1ebeb57946cfd7e998e5a7be78f2b0bc`, 01 Oct 2005): `UHARC.EXE` packed inputs we chose in
every mode, dictionary size, with and without multimedia detection and passwords, and as a
self-extractor, and `UNUHARC.EXE t` accepted every archive. Single-file archives of crafted
inputs show the stream byte for byte where the stream is not entropy coded (RLE, LZ78). Our
decoder reproduces every input byte for byte (SHA-256) where we hold the inputs, and every
file's stored CRC-32 where we do not, on:

- 24 committed fixtures (`Compression.Tests/Uharc/ReferenceVectors`), every ALZ selector among
  them;
- the same 17 MB of text and a 6.7 MB DLL packed with `-m2` and `-mz` (32 MiB dictionaries),
  `-mr`, `-mw`, `-mw -md1` and `-mx`, and further PPM sets listed under PPM;
- eight real-world archives from the dexvert sample set
  (`sembiance.com/fileFormatSamples/archive/uharc`): `Active`, `DEXVERT`, `TOOLS`, `Victoria`,
  `mix`, `new_carset.cuh`, `ntfs`, `tecgfx1` — 201 files, all ALZ:2/ALZ:3, dictionaries 1 KiB to
  4 MiB, selectors 1–6, 8 and 9 between them (no real sample uses 7).

## Archive layout

    [SFX stub]  header(39)  packed stream  CRC table  name table  file table

All integers are little-endian. *CRC* is CRC-32/IEEE (reflected `0xEDB88320`, initial and final
`0xFFFFFFFF`), the zip CRC.

A self-extractor (`-sfx`) is `UHARCSFX.EXE` with the archive appended: the archive begins where
the PE image's last section ends (`max(PointerToRawData + SizeOfRawData)`).

## Header

Bytes 4..34 are scrambled: byte `4 + i` is XORed with `(key + i * i) mod 256`, where `key` is
byte 37 — the low byte of the header check, which lies outside the scrambled range. The check
(bytes 37..38) is the low 16 bits of the CRC of the descrambled bytes 4..34.

| Offset | Size | Field |
| --- | --- | --- |
| 0 | 4 | `55 48 41 06`, "UHA" and version 6 |
| 4 | 2 | archive DOS time |
| 6 | 2 | archive DOS date |
| 8 | 1 | flags: bit 1 packed stream enciphered with the password, bit 2 tables enciphered with the password |
| 9 | 1 | filters: bit 0 executable filter enabled, bit 1 multimedia delta filter enabled |
| 10 | 1 | stream method (below) |
| 11 | 2 | number of directory names |
| 13 | 4 | unpacked size of the stream (sum of all file sizes) |
| 17 | 4 | packed size of the stream |
| 21 | 3 | number of files *N* |
| 24 | 3 | unpacked size of the name table |
| 27 | 3 | packed size of the name table |
| 30 | 3 | packed size of the file table |
| 33 | 2 | low 16 bits of CRC(CRC table ‖ name table), both unpacked |
| 35 | 2 | low 16 bits of CRC(file table), unpacked |
| 37 | 2 | header check |

`-pw` sets flags `0x06`. Filters measured: `0x01` for ALZ and PPM, `0x03` for LZP and LZ78, `0x00` for store and
RLE — the bits are set by mode, not by content; whether a file is actually filtered is decided
per file (see the filters).

### Stream method

Measured by packing the same 22 KB input in each mode and reading byte 10, then varying `-md`:

| Mode | Method byte | Name in `uharc l` |
| --- | --- | --- |
| `-m0` | 0 | store |
| `-mr` | 1 | RLE |
| `-mw` | 2..7 (`-md1` gave 2, the default 7) | LZ78 |
| `-mz` | 8..23 | LZP |
| `-mx` | 24..39 | PPM |
| `-m1` | 40..55 | ALZ:1 |
| `-m2` | 56..71 | ALZ:2 |
| `-m3` | 72..87 | ALZ:3 |

Within a band of 16 the offset is `log2(dictionary) − 10`: 1 KiB to 32 MiB. UHARC picks the
smallest power of two above the input when that is below `-md` (a 22 KB input under the default
`-md4096` is stored as 32 KiB, method 13 for `-mz`, 29 for `-mx`, 45/61/77 for `-m1/2/3`).

*Refuted:* labels 8..23 "ALZ", 24..39 "LZP + PPM" and 40..87 "PPM2". The tool's own `l` output
and the mode-to-byte measurement above put LZP, PPM and ALZ in that order.

## Tables

They follow the packed stream in the order CRC table (4*N* bytes, stored), name table, file
table. Each is enciphered on its own (the cipher restarts per table); the name and file tables
are then LZP streams (method 17, 512 KiB window) of the stated unpacked sizes; the file table
unpacks to 11*N* bytes.

- **CRC table**: *N* CRC-32 values, one per file.
- **Name table**: the directory names, then the *N* file names, each as a length byte and that
  many bytes (code page, `\` separated). The first directory is the root, the empty string.
  Directories without files appear only here; `-ed` stores them.
- **File table**, as planes of *N*:

  | Bytes | Content |
  | --- | --- |
  | 0 .. 2N | DOS **date** of each file, consecutive u16 |
  | 2N .. 4N | DOS **time** of each file, consecutive u16 |
  | 4N .. 6N | directory index: low bytes, then high bytes |
  | 6N .. 10N | size delta: byte 0 of each, then byte 1, 2, 3 |
  | 10N .. 11N | DOS attribute byte (`0x20` = archive) |

  Sizes run backwards: `size[i] = size[i−1] − delta[i]` (mod 2³², `size[−1] = 0`). Files lie
  in the stream in table order, which is not the order `uharc l` prints.

*Refuted:* "times, then dates" — the stored values decode
as invalid dates that way; dates first gives the times `uharc l -d2` prints.

## Table cipher

Keyed by the password — `"UHARC Default Password"` when there is none. A 256-byte state is
filled with the password length followed by the password repeated; then for `off` = 0, 8, …,
248 the CRC register (before its final complement) over the whole state is taken and XORed into
bytes `off..off+3` (little-endian) and its complement into `off+4..off+7`. Each byte of data is
then XORed with a keystream byte: with `p` starting at 0 and `m` at 254, `a = S[p]`,
`p = p+1`, `b = S[p]`, `m = m+1`, `S[p] = a + b`, output `a + b`, `S[m] = S[m] − b` (all
mod 256). The same transform, with the user password, covers the whole packed stream when
flags bit 1 is set. A wrong password shows up as a name table that does not decode or fails its
check.

## Range decoder

Shared by LZP, PPM and ALZ: 32-bit `code` primed with the stream's first five bytes (big-endian;
the first byte is always 0), `range = 2³² − 1`. Decoding against a total `t`:
`range /= t; slot = code / range`, then `code −= low·range; range *= size`, then while
`range < 2²⁴`: `code = code << 8 | next byte`, `range <<= 8`. A binary decision with a 12-bit
probability `p` of zero uses `range >>= 12` and adapts by `p += (4096 − p) >> s` on zero,
`p −= p >> s` on one. Past the end of the stream the coder reads zeros; a few are normal.

## Model resets

Neither coder stores reset points: both sides derive them from the file sizes. Starting from
the last reset point *r*, the coder looks *S* bytes ahead (*S* = 64 KiB for LZP, 128 KiB for
ALZ, or to the end) and resets at the first file end at or beyond `r + S`. LZP additionally,
when that file is not the first or the last and is smaller than 16 KiB, keeps moving the point
to the following file ends for as long as each following file is smaller than the one before.
The reset happens before the byte at the reset point is decoded. Tables are single streams and
never reset. Fixtures `reset-m2` / `reset-mz` (252 KiB over 18 files of 0 to 70 000 bytes) pin
both rules: any other choice of the span or the merge decodes garbage after the first reset.

## RLE (method 1)

Not entropy coded, so measured directly from the streams of crafted single-file archives. Two
marker bytes, a triple marker *T* and a run marker *R* = *T* + 1, start as `ED` and `EE`:

| Stream | Output |
| --- | --- |
| *T* `x` | `x` three times |
| *R* `n` `x`, 4 ≤ n ≤ 254 | `x` *n* times |
| *R* `FF` `lo` `hi` `x` | `x` repeated the 16-bit count (`AAAA…` of 70 000 is `EE FF FF FF 41 EE FF 71 11 41`) |
| *R* `c`, c ≤ 3 | *T* when c is even, *R* when odd, twice when c ≥ 2; then **both markers move down by 0x40** (`ED/EE` → `AD/AE` → `6D/6E` → `2D/2E` → `ED/EE`) |
| any other byte | itself |

The marker state runs on across file boundaries. The encoder writes runs of two as literals and
runs of three with *T*. Measured inputs: `x EE AE AD AAA y` packs as `78 EE 01 AE 01 AD 6D 41
79` — the escaped `EE` moves the markers to `AD/AE`, the escaped `AE` to `6D/6E`, after which
`AD` is a plain byte and `6D 41` is the triple.

## LZ78 (methods 2..7)

An LZW dictionary of 2^(method+8) phrases (`-md1` gives method 2, 1024 phrases; the default
method 7, 32768). Codes 0..255 are the bytes, 256 resets the dictionary, 257 is a run, phrases
start at 258. Bits are read most significant first.

**Codes.** A code is truncated binary over the codes defined so far: with *w* the current width
(9 at first) and *n* the next phrase number, read *w* − 1 bits as *v*; when `v + 1 ≥ 2^w − (n +
1)` one more bit decides whether the code is *v* or `2^w − 1 − v`. That is why plain bytes cost
eight bits while the dictionary is young: `ABCDEFGH` packs to its own eight bytes, `AAA` to `41
FD 80`. Once the width is above 9 and a byte code has been seen, each code is preceded by a flag
bit: 0 = an 8-bit byte follows (and the flag weight grows by 15), 1 = a phrase follows as *w* − 1
bits plus the same mirror bit, offset by 256 (and the weight halves); the flag disappears again
when the weight reaches 0.

**Phrases.** A code below *n* emits its phrase; the code *n* itself emits the previous phrase
plus its own first byte. After each phrase except the first after a reset, phrase *n* becomes the
previous phrase extended by the current phrase's first byte; *n* advances on every code and the
width grows when *n* reaches 2^w. Codes above *n* are ignored.

**Runs.** Code 257 is followed by a length: `1` + 8 bits, else `01` + 12 bits + 256, else `001`
+ 16 bits + 4352, else `000` + 32 bits + 4352; plus 256, then the byte (8 bits).

**Full dictionary.** When *n* reaches the capacity, every phrase that is no other phrase's
prefix is dropped, and the surviving phrases are renumbered downwards into the freed codes:
walking the codes from 0 upwards, the first child and then the next sibling of each phrase move
into the lowest free code whenever they lie above it (children listed newest first). The width
shrinks to fit the new *n*, and the next code starts a new chain without extending a phrase.

The fixtures `basic-mw` and `big-mw1` (a 1024-phrase dictionary refilled several times) pin the
codes and the pruning; every LZ78 archive packed here, up to the 17 MB pair, decodes exactly.

## LZP (methods 8..23)

The coder works on the complement of the data: history and window hold `b`, the output receives
`~b`. The window is the dictionary size; *hist* is the last four bytes as a 32-bit word.

**Prediction.** Before each byte, a candidate position is chosen:

1. after a run (the previous copy had length 255): the position at the last copy distance;
   decision 48, or 49 once three runs follow each other;
2. else, if the slot `(hist ^ hist >> 13) mod 2¹⁸` of a hash table remembers a position whose
   `hist` equals the current one: that position, decision 1;
3. else the position at the last copy distance, decision 9, if it is not within 260 bytes of
   being overwritten and the four bytes before it agree with *hist* in the low 16 bits, or in all but bits 8..15,
   or in the high 24 bits;
4. else the last position the current byte pair (low 16 bits of *hist*) occurred at, decision 17.

The hash slot and the pair table are then pointed at the current position. Nothing is predicted
when there is no candidate or it is within 256 bytes of being overwritten. Outside a run the
decision index moves by −1 when the hash slot remembers a length, +23 when it does not, plus a
two-bit history of recent outcomes (`[6,0,6,0,6,1,6,2,6,3,6,3,7,4,7,5]` indexed by
`history·2 + outcome`). If the candidate's byte is the one the previous copy stopped in front of,
the decision is skipped and counted as a miss. Each of the 50 decisions is a frequency pair
(split, total) seeded `11..14, 11..14, 11..14, 6..9 ×3, 49, 79` over 99 (see the decoder for the
exact row), `+64` per event, halved when the total reaches 16384. At a model reset the split
becomes `(split + 7) / 8` and the total `(total + 15) / 8`.

**Length.** On a hit, a length 1..254, or 255 for "run, predict again", is decoded by binary
search from node 6 over a fixed 256-node tree; the probability set is chosen by the remembered
length of the hash slot (or of the candidate's byte pair) through
`0,0,1,2,3,4,5,5,6,6,6,6,7…` (8 and 9 during runs), and the node's probability by whether that
remembered length is at most the node. Initial probabilities fall by 64 per set from 2560. The
decoded length is remembered in both places; a miss clears them.

**Literals.** 318 order-1 contexts of 256 frequencies plus one order-0 escape context (all 8).
The context is the last byte, except: the pair `FF FF` → 314, `D1 DF` → 315, `D3 DF` → 316, a
space (complemented `DF`) after a non-letter → 317, a letter after a non-letter → its value +123.
The byte the previous copy stopped in front of is excluded. Symbols occupy the interval from 255
downwards. An escape (weight starting at 1) falls back to the order-0 context restricted to
unseen bytes. A new symbol adds 32 to the escape weight and 16 to its order-0 frequency; every
symbol adds 32; a context halves (rounding up, escape weight `/4 + 1`) when frequencies plus
escape weight reach 16384.

## PPM (methods 24..39)

A context-mixing literal coder behind an LZP-style copy predictor. The window holds model
symbols — the complement of the output bytes, as in LZP — and the dictionary size of the method
band. Probabilities of binary decisions are 13-bit (total 8192) and move by 1/64 towards the
outcome; frequency tables use the range decoder as everywhere else. Constant tables are named
after `UharcPpmTables` in the decoder; where a row of numbers is the only specification, the
decoder holds it.

**Stream kind.** The stream starts with a uniform value of 2: 0 = text, 1 = binary. A text stream
then codes a length *L* as 11-bit groups (uniform 2048, least significant group first), each
followed by a uniform value of 2 that says whether another group follows. Output bytes before *L*
go through the word transform; at *L* the stream switches to binary coding (below). A stream of
nothing but text still codes *L*, usually a few bytes short of the end.

**Resets.** Like LZP's, derived from the file sizes, with a span of 256 KiB (text) or 64 KiB
(binary) and never beyond *L*. A reset clears the order-1 and order-0 rows and seeds the uniform rows, halves
the class rows, quarters the order-2 records, resets the copy decisions, the stride detector,
the cost averages and the linear-predictor scores, and finally codes the multimedia class as a
uniform value of 10 (0 = none, 1..9 as ALZ's selectors 1..9, with contexts 0xF8 lower). The
binary predictor's tables clear too.

**Copies.** Before every byte a candidate position is chosen:

1. during a run (the previous copy had length 255): the last copy distance, decision 0x50, or
   0x51 after more than two runs;
2. the newer of the two positions a deep hash of the last twelve symbols remembers, when its
   preceding bytes agree with the current ones over at least 48 symbols (12 once a streak
   counter — +1 per missed copy, + the length per copy at a new distance, halved per copy at
   the last one — reaches 16), decision 0x15;
3. the last copy distance, decision 0x0B, when its four preceding symbols agree with the
   current ones in the low 16 bits, in all but bits 8..15, or in the high 24 bits, and it is not
   within 260 bytes of being overwritten;
4. unless the hash slot of the last four symbols remembers a confident order-4 record (its
   byte 0x1A at least 0x40) and no copy length: the slot's position when the slot remembers the
   current context (decision 1), else the last copy distance (decision 0x1F).

Nothing is predicted within 256 bytes of being overwritten. Outside runs the decision index is
offset by a two-step outcome history (`LzpHistoryNext`) and by +0x27 when the slot remembers no
length, −1 when it does. If the candidate's byte is the one the previous copy stopped in front
of, the decision counts as a miss without being coded. A decision is a three-way frequency
table — escape (width 1), hit, miss — seeded `0, 1, LzpClassSeed, 99`, +32 per event and
halved at 16384; an *escape* codes a new multimedia class (uniform 10) and the decision is coded
again. A hit codes a length by binary search from node 14 over `LengthTree` with 12-bit
probabilities (shift 4) in one of six sets chosen by the run count or the remembered length;
lengths below a floor taken from `LengthFloor*` by the record hit statistics are not coded.
255 means a run. The bytes are read as the copy proceeds, so an overlapping copy repeats what it
just produced; the byte after the copy is excluded from the next literal.

**Literals** go through a cascade; the first stage that codes the byte ends it, an escaped
stage adds its symbols to the exclusions of the next (at most 23):

1. **Order-4 record** — 0x40000 records of 32 bytes: escape count, twelve counts and symbols
   (sorted by count), a hit history, the last-hit slot with a confidence nibble, a scale and the
   four context symbols as a tag; hashed `(c ^ c >> 13) mod 2^15` and chained 0x8000 apart.
   Used only when three gates agree (ratios of the record's and the order-2 record's counts and
   confidences, the stride statistics, and whether the record leaves anything after the
   exclusion). One symbol: a binary decision whose probability cell is chosen by the count, the
   agreement with the order-2 and order-1 predictions, recency and the word-boundary class. More
   symbols: up to four counts — the last-hit symbol, the order-1 prediction, the order-2
   prediction and the first symbol — are lifted by learned odds before a frequency decode in
   which the remaining total (or, below 128, an estimate from the escape-mass table) is the
   escape.
2. **Binary predictor** (binary streams). Eight sparse contexts (pairs of the last five bytes,
   the bytes at depth of an `FF` run, and the difference of the bytes one and two strides back)
   each remember a byte with miss and hit counts and a confidence. The most trusted proposes its
   byte; a binary decision whose masses add the order-4, order-2 and per-byte predictions' weights
   accepts it or excludes it.
3. **Order-2 record** — 20 bytes per pair of previous symbols (text streams use a second bank
   when the symbol before is a word-boundary symbol): escape count, eight counts and symbols, a
   novelty count, a running total and the last-hit slot. Used while the escapes stay below
   four fifths of the total and few new symbols arrived lately.
   Decoded like the order-4 record (lifts of the last-hit, the order-1 prediction and the first
   symbol), or by a binary decision when it holds one symbol.
4. **Frequency rows.** 0x30F rows of 256 frequencies: 0..0x1FF order-1 (text doubles them by
   the word-boundary flag of the byte before; in binary streams with a detected stride the row is
   `0x100 +` the byte one stride back), 0x200 order-0, 0x201..0x300 the multimedia classes, and
   0x301..0x30E uniform fallbacks a row escapes into. The byte a row saw last (with a
   confidence count) is coded first with an SSE probability; the rest by frequency, with an
   escape weight scaled by a learned factor. Every symbol is charged `65520 − ⌈4680·log2(i)⌉` for its 14-bit share *i*; the sum
   over the last 256 literals picks the row (below `0x6D4250`), a raw byte (from `0x91ADC0`
   up to `0xFEFF00`), or in between whichever of the row and order-0 has the lower decayed cost. In
   binary streams with a multimedia class, a linear predictor competes: when its decayed cost is
   lower, the byte is coded as a difference from the class's predicted byte in the class's row.

**Updates.** Every byte updates the order-4 and order-2 records, the rows it touched and the
per-row predicted byte. A new symbol in a full order-4 record takes the twelfth slot only while
that slot's count is below 6; in a full order-2 record the eighth slot only while its count is
below 12 — otherwise only the escape count and the total grow. Records halve when their totals
pass `Order4Threshold`/`Order2Threshold` for their escape count.

**Upkeep.** A stride detector counts the distances at which bytes repeat (a byte or its
neighbours) and follows the most frequent below 256; two marker counters detect interleaved
16-bit text, which then uses alternate bytes as context; binary streams remember for each byte
whether it occurred in the last 64 window positions (text streams do not, and the single-symbol
order-2 decision never asks).

**Text transform.** In a text stream the output of every symbol below *L* is `~symbol`, except
that complemented values from 0x80 stand for strings (`TransformString*`), a capital-letter
prefix that subtracts 0x20 from the next letter, or an escape whose next symbol selects a byte
from `TransformEscape`. Symbols 4..6 do not enter the context (they become `DF` once).

**Switch to binary.** At *L* the window is rewritten as the bytes the transform produced
(complemented, from half a window back), the order-4 records are quartered, the order-2
records and copy hashes cleared, the hashes and the order-4 records are rebuilt over the last
32 KiB of the rewritten window, the learned probabilities are seeded afresh and coding continues
as a binary stream.

**Executable filter.** PPM streams store an `E8`/`E9` operand with its bytes 0 and 2 swapped
when either the stored value or the swapped one is a target within the file; undo the swap,
then proceed as for the other methods.

Fixtures `basic-mx` (text, switching to binary after 15 bytes), `reset-mx` and `reset-mx1`
(reset points; a 1 KiB window wrapping hundreds of times), `exe-mx` and `mm-mx` pin the stream;
all archives packed here — 17 MB of text, 13 MB of DLLs, fonts and media, the source and
documentation of this repository, mixed text and binary sets, dictionaries from 1 KiB to 16 MiB
— decode exactly, and so does the real-world sample `D3X.UHA` (45 files).

*Refuted*, each by the first byte it breaks:
- a new symbol always taking the last slot of a full record (the obvious rule): breaks the
  17 MB text packed as binary at 38 450 (order-4) and a DLL set at 536 014 (order-2); the
  thresholds 5 and 7, 11 and 13 break other corpora;
- the recency flag in the single-symbol order-2 decision of a text stream: breaks a 1 KiB
  window text archive at 3 931, where the window position returns to 0.

## ALZ (methods 40..87)

LZ77 with an adaptive token model, a cost-switched literal coder and, for multimedia data,
predictive literal coding. ALZ:1, ALZ:2 and ALZ:3 decode identically; the band only reflects
the encoder's effort.

**Start.** Before anything else, and again at each model reset, the stream codes a selector as a
uniform value of 11.

**Tokens.** Each step decodes one of five tokens from cumulative frequencies seeded
`[0,1,34,45,57,101]` (rows 0..63) or `[0,1,28,78,89,100]` (rows 64, 65 after one or several
runs): 0 = new selector, 1 = copy with a new distance, 2 = copy at the last distance, 3 = copy
at one of the older distances, 4 = literal. The row is 64/65 during runs; otherwise a literal
mode 0..31 driven by a fixed transition table on each token, which for modes 0..15 is replaced
by `32 + tag/8` when the two-byte context's remembered token tag says so (a tag is the token in
the top two bits and a repeat count below). A token adds 88 to its frequency; the row halves at
16384.

**Copies.** Twelve recent distances, all 1 at first. A new distance: a slot 1..44 by binary
search (root 16), slot *k* covering `[base, base + span)` with bases 1, 2, 4, 8, 16, 32, then
64, 96, 128, 192, … (2ᵏ and 1.5·2ᵏ alternating), the span clipped to what has been output; the low bit coded
with its own probability when the span is ≥ 4, then a uniform value, split into 2048-wide
blocks when wider. Token 3 picks slot 1..11 of the recent list by a small tree. Lengths are
decoded by binary search from node 10 over a 256-node tree, in one of eight probability sets
(the distance class < 256, < 4 Ki, < 64 Ki, < 1 Mi, larger for new distances; one set for
repeats and two for runs), each node's probability refined by the two lengths last seen after
the copy's source byte pair. Short lengths are not coded at all: a new distance of class *c*
copies at least *c* + 4 bytes, a repeat at least 3. 255 means a run: the next token is decoded
in the run rows. The distance enters the recent list unless it is 1 and fewer than two runs
precede it; a repeat moves its slot to the front.

**Literals.** 1056 frequency contexts of 256 bytes, each with an escape weight and an
adaptation budget, plus escape contexts. Plain text uses the last byte (offset 256 when its top
two bits equal the byte before's) with an escape context chosen by the last byte's top three
bits; selector 10 switches to alternative contexts (`0x200 +` last byte, escape `0x401`) and
halves them. The byte that would have continued the last copy is excluded. Every coded symbol
is charged a cost `65520 − ⌈4680·log2(16384·p)⌉`; the sum over the last 256 literals picks
how the next is coded: below `0x6D4250` (≈ 6 bits/byte) by context, at or above `0x91ADC0`
(≈ 8 bits/byte) as a raw byte, in between by whichever of context or an order-0 model has the
lower decayed cost (`·31/32`) — while the other is still updated and charged as if it had been
used.

**Multimedia selectors.** While selector 1..9 is active, a literal is coded as the difference
from a predicted byte (with a sign that flips whenever the difference has its top bit set) in a
context chosen by the predictor, and every output byte — copied ones included — updates it:

| Selector | Record | Predictor | Triggered here by |
| --- | --- | --- | --- |
| 1 | 1 byte | previous byte, context by the last two difference classes | an 8-bit grey ramp |
| 2 | 2 bytes | byte two back, context by phase and difference class | a slowly rising 16-bit counter |
| 3 | 3 bytes | byte three back, or a gradient once it hits more than 8 times | a 24-bit BMP |
| 4 | 4 bytes | byte four back | 16-bit RGB565 pixels |
| 5 | 1 | adaptive linear predictor with pitch search, unsigned | an 8-bit mono WAV |
| 6 | 1 | the same, signed | a signed 8-bit sine |
| 7 | 2 | per-channel linear predictor, unsigned | an 8-bit stereo WAV |
| 8 | 2 | per-lane linear predictor, signed | 16-bit mono PCM |
| 9 | 4 | per-lane linear predictor, signed, cross-lane term from two lanes back | 16-bit stereo WAV, big-endian stereo, RGBA |

Which data UHARC hands to which selector is the encoder's choice; the decoder only follows the
stream.

The linear predictors keep, per phase, the last sample, four deltas, five weights in ±32 that
move by one every 16 samples towards whichever of eleven trial perturbations had the least
folded error, and two cost sums deciding between prediction and plain repetition. A phase whose
last four differences were all zero uses its own "flat" context: `base + record size` for
selectors 8 and 9, and one of two finer classes for 5..7. Switching to any selector from 5 on
clears all predictor state.

*Refuted*, each by a fixture that fails at the first wrong byte:
- flat context `base + 4` for selector 8: it is `base + 2`; with `+4` it collides with
  selector 9's phase-1 context and the stream breaks two bytes after the switch from 8 to 9;
- keeping predictor state across a switch between selectors ≥ 5: breaks on the first byte.

## Filters

Applied to each file after decoding, before its CRC is checked.

**Executable filter** (filters bit 0: ALZ, LZP, LZ78, PPM). A file of at least 1024 bytes whose first
word is `MZ`, `ZM` or `MQ`, or that starts `7F 'ELF'` with machine 3, or `FBOV`, was
preprocessed. For MZ with 16..8192 relocations and a relocation table at ≥ 0x1C inside the file,
the table was stored as four byte planes and delta-coded per 16-bit word against the word four
bytes back; restore that, then scan from its end (otherwise from offset 8 or 0x1A for MZ, 0x14 for ELF, 0
for FBOV). Every
`E8`/`E9` operand that is a positive value below the file size was an absolute target: subtract
the position (0 stands for the position itself); a negative one within reach was relative: add
the file size; others whose high half is not trivial carry, in their low half, either `0x8000 +
index` into a 128-entry move-to-front list of recent targets (initially 0..127) or a target to
be entered into that list. The scan advances 5 bytes past a rewritten operand, otherwise 4.
PPM streams first undo a swap of the operand's bytes 0 and 2 (see PPM); an operand whose swap
was undone advances 5 bytes even when nothing else rewrites it.

*Refuted:* a zero-initialised list and a 16-byte minimum — `UNUHARC.EXE` packed as
data fails its CRC under the former, a 364-byte MZ file under the latter.

**Multimedia filter** (filters bit 1, LZP and LZ78). Files of at least 1024 bytes recognised as
24-bit uncompressed BMP, PCM WAV (8 or 16 bit, mono or stereo) or Creative VOC were delta-coded
from byte 36 with a stride of 3, channels × bytes per sample, or 1: each byte is the difference
from the byte one stride back, with a per-phase sign that flips on a difference ≥ 128.

## Writing

The writer produces 0.6 archives in the layout above: the stream stored (method 0) or packed
with LZP at the smallest window that holds it (methods 8..23, 1 KiB to 32 MiB), the three tables
— the name table lists the root as an empty name and every directory with a trailing `\` —
packed with LZP method 17 and enciphered with the default password, the header scrambled with
the low byte of its own check. The filters byte is 0: no executable or multimedia filter runs,
so none is undone. It writes no password-protected archives and no other method.

The LZP encoder keeps the decoder's model step for step and answers each prediction with the
input: a hit with the longest copy the candidate reproduces (255 when it runs on, the rest of
the input at its end), otherwise a miss. Copies of one byte have no length code (the length
tree covers 2..254), so a single matching byte is a miss followed by that byte as a literal —
which the decoder expects: the byte after it is then excluded like the byte after a copy. The
range encoder is the decoder's mirror with a carrying 64-bit low, which makes the first stream
byte 0.

Every archive written from the fixture inputs, 17 MB of text, 13 MB of DLLs, fonts and media,
this repository's sources and the mixed text/binary set — stored and packed, up to 400 files and
a 32 MiB window — is extracted byte for byte by `UHARC.EXE x` and `UNUHARC.EXE x` (0.6b),
directories and empty files included.

## UHARC 0.2

UHARC 0.2 (21 Dec 1997, a DOS program; `UHARC.EXE` from the archive.org item `UHARC`, MD5
`6e2f0e8d0e6b83c0c05c9b406ce056fb`) writes a different container and different coders. It was
run in DOSBox on the fixture inputs in every mode and dictionary size; all its archives and the
four 0.2 samples of the dexvert set (`BEAT`, `MENUS`, `MUSIC`, `TEAM`: 66 files, 35 MB, all
`-m3` with 2 MiB) decode byte for byte or pass every CRC.

    header(48)  packed stream  CRC table  name table  date/time table  size table

**Header.** Bytes 4..43 are scrambled like 0.6's, with the key in byte 44; bytes 44..47 hold the
full CRC-32 of the descrambled bytes 4..43.

| Offset | Size | Field |
| --- | --- | --- |
| 0 | 4 | `55 48 41 02` |
| 4 | 2 | archive DOS time |
| 6 | 2 | archive DOS date |
| 9 | 1 | stream method |
| 10 | 2 | number of directory names |
| 12 | 4 | number of files *N* |
| 16 | 4 | unpacked size of the stream |
| 20 | 4 | packed size of the stream |
| 24 | 4 | unpacked size of the name table |
| 28 | 4 | packed size of the name table |
| 32 | 4 | packed size of the date/time table |
| 36 | 4 | packed size of the size table |
| 40 | 4 | CRC-32 over all four tables, unpacked, in order |
| 44 | 4 | header check |

Methods: 0 store, 1 RLE, 2..7 LZW, 8..19 `-m1`, 20..31 `-m2`, 32..43 `-m3`, the dictionary
`1 << ((m − 8) mod 12 + 10)` (1 KiB to 2 MiB; `-md-` gives 1 KiB). The tool writes no stream
with methods 1..7; the tables use LZW.

**Tables.** Each is enciphered on its own: a 256-byte state filled with
`"UHARC Default Password"` repeated (no length byte), keyed by the same CRC rounds as 0.6's
cipher, then `a = S[p]; p = p + 1; S[p] += a; byte ^= S[p]`. The CRC table (4*N*) is stored, the
name table LZW method 7, the date/time table LZW method 2 (*N* dates, then *N* times), the size
table LZW method 7 (7*N*: *N* size deltas as 32-bit words, *N* directory indices as 16-bit
words, *N* attribute bytes). Names and sizes work as in 0.6; names are DOS 8.3 in upper case.

**LZW** (methods 2..7). A dictionary of `2^(method+8)` codes of parent and byte, codes 0..255
the bytes, 256 restart, 257 run, phrases from 258. Codes are truncated binary like 0.6's LZ78
but read least significant bit first, the width growing from 9 to `method + 8` bits. The first
code (and the first after a restart) is a plain byte. A run is a 16-bit count (high byte first)
and the byte, preceded on the first run after a phrase by one more literal byte; the phrase
after a run then omits its first byte. A code at or above the next free one repeats the previous
phrase plus its first byte. Each phrase adds the previous phrase plus its own first byte.

**Main coder** (methods 8..43). A range coder that reads the stream bit by bit (most
significant first): `code` starts with 16 bits, `range = 0x10000`; a value in `[0, t)` is
`((code + 1)·t − 1) / range`; narrowing to `[l, h)` subtracts `l·range/t` from `code`, sets
`range = h·range/t − l·range/t` and doubles it, shifting in a bit, while it is at most `0x8000`.

Each step decodes a token from one of eight cumulative rows (seed `0, 1, 2, 0xC00, 0x2000`):
0 control, 1 run, 2 copy, 3 literal; a copy adds 64 to its width, copies and literals add 64
to the total, the upper two widths halve at 16384, and the row becomes
`(token + 2·row − 2) mod 8` — controls and runs leave both alone.

- **Control** (uniform 12): 0..5 select a multimedia mode (0 none) and, once output exists,
  reset the literal contexts; 6..9 set the literal step to 2, 8, 32 or 128 and halve the
  contexts by a table of the old and new step; 10 toggles raw literals (uniform 256); 11
  toggles literals restricted to 0x80..0xFF.
- **Literal**: 256 order-1 contexts of 256 frequencies (all 1 at first), kept as a cumulative
  tree whose symbols are ordered from 255 down; the step is added, a context halves (rounding
  up) at 16384. The next context is the symbol, or in the multimedia modes a class of it combined
  with the position modulo the mode's period. The output is the symbol's complement — in a
  multimedia mode a difference from the byte `((mode − 1) mod 4) + 1` positions back, with a sign
  that flips on every difference ≥ 128.
- **Copy**: a distance class from a 45-entry cumulative model. Classes 0..39 cover distances
  from 2 up to 2 MiB in bands that halve towards the short end, and enter the model only once the
  history reaches their first distance; 40 is distance 1; 43 reuses the newest of the recent
  distances, 42 the second or third, 41 an older one (the list holds 5, 8 or 16 for `-m1`, `-m2`,
  `-m3`). A new distance is refined within its band by uniform values (in 4096-wide blocks when
  wider) and enters the list; a reused one moves to its front. Then a length class (22 classes,
  separate models for new and reused distances) plus a uniform extra; every copied byte also
  goes through the literal model.
- **Run**: a length class of three (`0x100 +` one, two or three bytes), then the byte; the
  history receives 255 copies, the output the full run (only the first five bytes pass the
  output transform), and the literal model counts every byte.

**Executable filter.** Files of at least 1 KiB that start `MZ` or `ZM` had every `E8`/`E9`
operand that is a target within the file made absolute (positive below the size: subtract the
position; negative within reach: add the size); the scan advances 5 bytes past a rewritten
operand, otherwise 4. Stored archives (`-m0`) store executables unchanged.

Fixtures `v02-basic-m2`, `v02-exe-m0`, `v02-exe-m2`, `v02-reset-m3`, `v02-reset-md`
(a 1 KiB history), `v02-mm-m3` and `v02-runs-m2`; the 17 MB pair packed with `-m3` decodes
exactly as well.

*Refuted:* times before dates in the date/time table (the dates then fall in 2019..2048 for the
dexvert samples), directory indices as two byte planes (subdirectories then point outside the
name table), and the executable filter on stored archives (fails `v02-exe-m0`'s CRC).

## What is not derived

- **Versions 0.3 and 0.4** (`UHA` + 3 or 4): their headers, ciphers and coders differ from both
  0.2 and 0.6; no samples are at hand; refused with the version named.
- **UHARC 0.2's RLE stream method 1**: no archive the tool writes uses it; refused.
- **Writing** any method other than store and LZP, passwords, filters and SFX stubs.

## Sources

- UHARC 0.6b, `UHARC.DOC` — modes, switches, dictionary rule, SFX.
- UHARC 0.2, `UHARC.DOC` (21 Dec 1997) — modes and switches of 0.2.
- Archive Team, "UHARC", <http://fileformats.archiveteam.org/wiki/UHARC> — overview, samples.
