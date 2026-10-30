# UHARC 0.6 — on-disk notes

UHARC is Uwe Herklotz's closed-source archiver (1997–2005). No specification was ever
published and no implementation exists under a licence this repository can take, so this
document is the specification `FileFormat.Uharc` is implemented from. It covers the 0.6 format
(0.6, 0.6a and 0.6b write the same archives; the documentation says so and the header carries
version byte 6) as far as it was established; what is not covered is listed at the end.

**Status: read-only, partial.** Container, ciphers, the store, LZP and ALZ modes, ALZ's nine
multimedia predictors, the executable and multimedia filters, passwords and SFX stubs are
established and verified. PPM (`-mx`), RLE (`-mr`), LZ78 (`-mw`) and archives of earlier
versions are not, and are refused.

## How this was established

Sourcing rung 2 of AGENTS.md. Two third-party decoders exist and neither can be taken:

- **OpenUHA** (Free Pascal, GPL-2.0, `Xelitan/OpenUHA---Pure-Pascal-UHA-unpacker` at
  `61662b09`) — read as a description of the LZP and ALZ coders and of the container.
- **unuharc_recon** (C89, no licence, `roytam1/unuharc_recon` at `056cf7e3`) — its `FORMAT.md`
  was the map of the container; where the two decoders disagreed with each other or with the
  archives, its code was read to find out which reading was right.

No code was taken from either. Both were also built locally and run as black-box oracles,
one of them instrumented to print the predictor state per byte so the first diverging byte of
a failing stream could be located; neither build is distributed.

The authority is **UHARC 0.6b itself** (`uharc06b.zip`, archive.org item `uharc06b`, MD5
`1ebeb57946cfd7e998e5a7be78f2b0bc`, 01 Oct 2005): `UHARC.EXE` packed inputs we generated in
every mode, dictionary size, with and without multimedia detection and passwords, and as a
self-extractor; `UNUHARC.EXE t` accepted every one. Our decoder reproduces every input byte for
byte (SHA-256) where we hold the inputs, and every file's stored CRC-32 where we do not, on:

- 16 committed fixtures (`Compression.Tests/Uharc/ReferenceVectors`), every ALZ selector among
  them;
- a 17 MB, 32 MiB-dictionary pair (`-m2`, `-mz`) of text and a 6.7 MB DLL;
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

*Refuted:* the unlicensed reconstruction's `FORMAT.md` labels 8..23 "ALZ", 24..39 "LZP + PPM"
and 40..87 "PPM2". The tool's own `l` output and the mode-to-byte measurement above put LZP,
PPM and ALZ in that order.

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

*Refuted:* "times, then dates" (the reconstruction's `FORMAT.md`) — the stored values decode
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

Shared by LZP and ALZ: 32-bit `code` primed with the stream's first five bytes (big-endian;
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
- flat context `base + 4` for selector 8 (OpenUHA): it is `base + 2`; with `+4` it collides with
  selector 9's phase-1 context and the stream breaks two bytes after the switch from 8 to 9;
- keeping predictor state across a switch between selectors ≥ 5: breaks on the first byte.

## Filters

Applied to each file after decoding, before its CRC is checked.

**Executable filter** (filters bit 0, ALZ/LZP only). A file of at least 1024 bytes whose first
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

*Refuted:* a zero-initialised list and a 16-byte minimum (OpenUHA) — `UNUHARC.EXE` packed as
data fails its CRC under the former, a 364-byte MZ file under the latter.

**Multimedia filter** (filters bit 1, LZP and LZ78). Files of at least 1024 bytes recognised as
24-bit uncompressed BMP, PCM WAV (8 or 16 bit, mono or stereo) or Creative VOC were delta-coded
from byte 36 with a stride of 3, channels × bytes per sample, or 1: each byte is the difference
from the byte one stride back, with a per-phase sign that flips on a difference ≥ 128.

## What is not derived

- **PPM** (`-mx`, methods 24..39): an order-4-2-1-0 model behind an LZP pass, with its own
  executable pre-transform. Neither third-party decoder is a usable description of it (OpenUHA's
  decodes our 22 KB sample and none of the larger ones); archives list, extraction is refused.
- **RLE** (`-mr`) and **LZ78** (`-mw`): rare, flagged "may be not supported in future versions"
  by the author; refused.
- **Versions before 0.6** (`UHA` + 2, 3 or 4; four dexvert samples are 0.2): their headers,
  ciphers and codecs differ; refused with the version named.
- **Writing.** Nothing here writes UHARC.

## Sources

- UHARC 0.6b, `UHARC.DOC` — modes, switches, dictionary rule, SFX.
- Archive Team, "UHARC", <http://fileformats.archiveteam.org/wiki/UHARC> — overview, samples.
- OpenUHA and unuharc_recon as described above (read, not taken).
