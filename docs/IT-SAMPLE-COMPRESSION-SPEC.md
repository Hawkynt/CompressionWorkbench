# Impulse Tracker compressed samples (IT214 / IT215) — implementation specification

Status: **implemented** — `Hawkynt.FileFormats.Audio/Codecs/Codec.TrackerXmIt/ItSampleDecompressor.cs`
was rewritten clean-room from this document. Reference modules and oracle digests (libxmp,
libopenmpt): `Compression.Tests/Codecs/TrackerXmIt/ReferenceVectors/README.md`.

Why a replacement was needed: the previous decoder called itself "a direct port of the
schismtracker reference decoder (`fmt/it.c`: `it_decompress8` / `it_decompress16`)". Schism
Tracker is GPL-2.0-or-later (the file that holds those routines, `fmt/compression.c`, carries
the GPL header), which this LGPL-3.0-or-later repository cannot absorb (see
[`LICENCE-PROVENANCE.md`](LICENCE-PROVENANCE.md)). Whoever writes the replacement must not
have read Schism Tracker's decompressor or the current `ItSampleDecompressor.cs`.

Licence-compatible material the replacement **may** consult: OpenMPT's
`soundlib/ITCompression.cpp` (BSD-3-Clause — compatible, but if code is taken from it, it is a
rung-1 conversion and needs a notice), the Impulse Tracker `ITTECH.TXT` technical document,
and this file. libxmp's `itsex.c` is usable as an oracle only after its licence has been
checked for the revision in hand.

---

## 1. Scope and public surface

The replacement keeps the current entry points, which `ItSample.cs` and
`FileFormat.Its/ItsSampleDecoder.cs` call:

- `sbyte[] ItSampleDecompressor.Decompress8(ReadOnlySpan<byte> compressed, int lengthSamples, bool it215)`
- `short[] ItSampleDecompressor.Decompress16(ReadOnlySpan<byte> compressed, int lengthSamples, bool it215)`

`compressed` starts at the sample's data pointer and may extend past the sample's own data
(the caller passes the rest of the file). `lengthSamples` is the sample length from the sample
header. Compression is decode-only.

Container-level facts the callers already handle (listed so the decoder's contract is clear):

| Sample header field | Bit | Meaning |
|---|---|---|
| `Flags` | 0x02 | 16-bit samples (else 8-bit) |
| `Flags` | 0x04 | stereo (see §6) |
| `Flags` | 0x08 | compressed — this format |
| `Cvt` | 0x04 | IT 2.15 compression (double delta), else IT 2.14 |

## 2. Stream structure

The compressed data is a sequence of **blocks**. Each block is:

| Offset | Size | Field |
|---|---|---|
| 0 | 2 | `n` — number of packed bytes that follow, unsigned little-endian |
| 2 | `n` | packed bit stream |

The next block starts immediately after the `n` packed bytes. A block decodes to at most
**0x8000 bytes of sample data**: 32768 samples for 8-bit, 16384 samples for 16-bit. Every
block except the last decodes exactly that many; the last decodes the remainder of
`lengthSamples`.

At the start of every block the decoder resets its state: bit width to the maximum
(9 for 8-bit, 17 for 16-bit) and both integrators (§4) to zero. Nothing carries between blocks.

## 3. Bit reading

Within a block, bits are taken least-significant-bit first from each byte, bytes in order. An
`w`-bit read assembles the value LSB-first: the first bit read is bit 0 of the result.
Reads that run past the block's `n` bytes return zero bits —
real encoders end blocks short of the last code's final bits, so this must not be an error.
A block whose header, or whose `n` packed bytes, lie past the end of the buffer is truncated
input and is refused (`InvalidDataException`): the missing samples cannot be known, and inventing
them would return wrong bytes. (An earlier draft read such a header as `n = 0`; the implementation
refuses instead; none of the 300-odd real compressed samples it was checked against needs the
lenient reading.)

## 4. Decoding a block

State: width `w` (bits per code), integrators `d1` and `d2`, wrapping at the sample size
(8-bit two's-complement for `Decompress8`, 16-bit for `Decompress16`).

Repeat until the block's sample count has been produced:

1. If `w` is outside 1..`W` (`W` = 9 for 8-bit, 17 for 16-bit) the stream is invalid
   (throw `InvalidDataException`).
2. Read `w` bits into `v`.
3. **Width-change escapes.** An escape changes `w` and produces **no** sample:

   | Current width | Escape condition | New width |
   |---|---|---|
   | 1..6 | `v == 1 << (w − 1)` | read `k` more bits (`k` = 3 for 8-bit, 4 for 16-bit) into `t`; `n = t + 1` |
   | 7..`W`−1 | `B < v ≤ B + 2c` where `B = (M >> (W − w)) − c`, `M` = 0xFF / 0xFFFF, `c` = 4 / 8 (8-bit / 16-bit) | `n = v − B` |
   | `W` | bit `W−1` of `v` is set (0x100 / 0x10000) | `w = (v & M) + 1` directly |

   For the first two rows, the new width is `n` if `n < w`, otherwise `n + 1` (a code never
   re-selects the width it is already at). Continue with step 1.
4. **Sample.** Otherwise `v` is a delta: if `w` is less than the sample size (8 or 16),
   sign-extend `v` from `w` bits; otherwise take its low 8 / 16 bits as a signed value.
5. **Integrate.** `d1 = d1 + delta`, `d2 = d2 + d1` (both wrapping). Emit `d2` when `it215`,
   else `d1`.

Worked boundaries for 8-bit, width 7: `B = (0xFF >> 2) − 4 = 59`, escapes are 60..67; width 8:
`B = 123`, escapes are 124..131. For 16-bit, width 16: `B = (0xFFFF >> 1) − 8 = 32759`, escapes
are 32760..32775.

The output length is exactly `lengthSamples`; the loop over blocks ends when that many samples
have been produced, whatever bytes follow.

## 5. Errors

| Condition | Behaviour |
|---|---|
| width leaves 1..`W` after an escape (only possible from the width-`W` escape) | `InvalidDataException` |
| a block's bit stream ends before its last code | missing bits read as zero; samples still produced |
| a block header or its `n` bytes run past the end of the data | `InvalidDataException` |
| `lengthSamples ≤ 0` | empty array |

`ItSample.cs` catches exceptions and falls back to an empty sample; the decoder itself must
not swallow them.

## 6. Stereo (hypothesis — not confirmed against the original tracker)

OpenMPT stores the two channels of a compressed stereo sample one after another, each a full
block sequence of `lengthSamples` samples. The current callers ignore the stereo flag for
compressed samples; the replacement need not add stereo support, but if it does, it must be
pinned by a real IT file with a compressed stereo sample.

## 7. Reference vectors to capture

Commit each with a note naming the producing tool, version and settings.

1. **Real modules**: at least one `.it` file saved by Impulse Tracker 2.14 with compressed
   8-bit samples, one with 16-bit samples, and one saved with IT 2.15 compression (`Cvt & 4`).
   The expected PCM is the sample exported uncompressed by OpenMPT (BSD-3-Clause) — save the
   module once compressed and once uncompressed and compare.
2. **OpenMPT-compressed synthetic samples** (OpenMPT can write IT-compressed samples): for
   both widths and both IT214/IT215: silence; a full-scale square wave (forces width 9/17 and
   the width-`W` escape); a slow sine (narrow widths, the 1..6 escape); white noise; a ramp
   that crosses the 7..`W`−1 escape band.
3. **Block boundaries**: an 8-bit sample of exactly 32768 samples, one of 32769 (second block
   of one sample), and a 16-bit sample of 16384 / 16385 samples.
4. **Truncation**: vector 2's noise sample with the last packed byte removed — the decoder must
   still return `lengthSamples` samples, matching OpenMPT's output for the same truncated data
   if OpenMPT accepts it.
5. **Black-box cross-check** (allowed, no code taken): decode the same files with Schism
   Tracker or libxmp and compare PCM.
