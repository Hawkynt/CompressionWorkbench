# G.722 64 kbit/s sub-band ADPCM — implementation specification

Status: **specification for a clean-room replacement of `Hawkynt.FileFormats.Audio/Codecs/Codec.G722/G722Codec.cs`.**

Why a replacement is needed: the current codec calls itself a port of the SpanDSP `g722.c`
(Steve Underwood). SpanDSP licenses that file under the GNU LGPL **version 2.1 only** — there
is no "or any later version" clause — and LGPL-2.1-only code cannot be redistributed under
this repository's LGPL-3.0-or-later (see [`LICENCE-PROVENANCE.md`](LICENCE-PROVENANCE.md)).
Whoever writes the replacement must not have read SpanDSP's `g722.c` or the current
`G722Codec.cs`; this document is meant to be enough on its own.

Normative source: **ITU-T Recommendation G.722 (09/2012), "7 kHz audio-coding within
64 kbit/s"**, sections 3 (encoder), 4 (decoder) and 6 (computational details, blocks 1L–6L,
1H–5H, 4L/4H sub-blocks). The block and variable names below (`SL`, `DETL`, `NBL`, `QM4`,
`RL42`, `UPPOL2` …) are the Recommendation's own. Where this document and the Recommendation
disagree on anything other than the I/O conventions in §2, the Recommendation wins and the
disagreement is a defect in this document.

---

## 1. Scope

| Item | Required |
|---|---|
| Mode | Mode 1 only: 64 kbit/s, 6 low-band bits + 2 high-band bits per octet |
| Directions | Encoder and decoder |
| Sample format | 16-bit signed linear PCM, 16 kHz, mono |
| Modes 2/3 (56/48 kbit/s) | Out of scope (the decoder may later drop 1 or 2 low-band LSBs, §6) |
| Packed bitstreams (`G722_PACKED`-style bit packing) | Out of scope: one octet per codeword |
| 8 kHz "narrowband" output | Out of scope |

Public surface the replacement must keep (the rest of the codebase calls it):

- `byte[] G722Codec.Encode(ReadOnlySpan<short> pcm)` — one output octet per **two** input
  samples; a trailing odd sample is dropped.
- `short[] G722Codec.Decode(ReadOnlySpan<byte> data)` — two output samples per octet.

Both start from the reset state of §3 on every call (no state carries across calls).

## 2. I/O conventions (where this codec deviates from the Recommendation's 14-bit interface)

The Recommendation specifies a 14-bit uniform PCM interface. This codec, like the common
16-bit software implementations, feeds 16-bit samples straight into the transmit QMF and
compensates with the shift amounts below. These shifts are part of the contract — streams
must interoperate with FFmpeg's `g722` encoder/decoder (see §7).

- Transmit QMF output: `XL = (sumEven + sumOdd) >> 14`, `XH = (sumEven − sumOdd) >> 14`
  (arithmetic shift).
- Receive QMF output: each of the two output samples is `saturate16(accumulator >> 11)`.
- Octet layout: bits 7–6 = high-band code `IH` (2 bits), bits 5–0 = low-band code `IL`
  (6 bits). Octets are emitted in time order.
- Sample order: within each QMF frame the **earlier** input sample is pushed into the delay
  line first; on output the sample computed from the odd-indexed taps is emitted first (§4.6).

`saturate16(v)` clamps to [−32768, 32767]. `>>` is an arithmetic right shift on signed 32-bit
integers; all products below fit in 32 bits.

## 3. State and reset

Two independent band states, low (L) and high (H). Each holds:

| Field | Meaning | Reset value |
|---|---|---|
| `S` | signal estimate (`SL`/`SH`) | 0 |
| `SP` | pole-section estimate | 0 |
| `SZ` | zero-section estimate | 0 |
| `R[0..2]` | reconstructed-signal history (`RLT`) | 0 |
| `P[0..2]` | partial reconstructed history (`PLT`) | 0 |
| `A[1..2]`, `AP[1..2]` | pole coefficients and their next values | 0 |
| `D[0..6]` | quantised difference history (`DLT`) | 0 |
| `B[1..6]`, `BP[1..6]` | zero coefficients and their next values | 0 |
| `NB` | log scale factor (`NBL`/`NBH`) | 0 |
| `DET` | quantiser scale (`DETL`/`DETH`) | **32** for L, **8** for H |

Plus one 24-entry QMF delay line per direction, all zero.

## 4. Algorithm

### 4.1 Transmit QMF (encoder, once per two input samples)

Delay line `x[0..23]`: shift every entry down by two (`x[i] ← x[i+2]` for i = 0..21), then
`x[22] ← earlier sample`, `x[23] ← later sample`.

Coefficients (12 taps, mirrored — the Recommendation's 24-tap filter uses each twice):

```
h = [ 3, -11, 12, 32, -210, 951, 3876, -805, 362, -156, 53, -11 ]
sumOdd  = Σ_{i=0..11} x[2i]   · h[i]
sumEven = Σ_{i=0..11} x[2i+1] · h[11−i]
```

then `XL`, `XH` per §2.

### 4.2 Low band, encoder (blocks 1L, 2L, 3L, 4L)

1. Difference: `EL = saturate16(XL − SL)`.
2. Magnitude: `WD = EL` if `EL ≥ 0`, else `−(EL + 1)`.
3. Quantise: the level index `m` is the smallest `m` in 1..29 with
   `WD < (Q6[m] · DETL) >> 12`; if there is none, `m = 30`.
4. Code: `IL = ILP[m]` if `EL ≥ 0`, else `ILN[m]`.
5. Run the low-band adaptation of §4.4 with the 4-bit truncated code `IL >> 2`.

### 4.3 High band, encoder (blocks 1H, 2H, 3H, 4H)

1. `EH = saturate16(XH − SH)`; `WD` as in 4.2.
2. `m = 2` if `WD ≥ (564 · DETH) >> 12`, else `m = 1`.
3. `IH = IHP[m]` if `EH ≥ 0`, else `IHN[m]`.
4. Run the high-band adaptation of §4.4 with `IH`.

The octet is `(IH << 6) | IL`.

### 4.4 Adaptation shared by encoder and decoder

**Low band**, given the 4-bit code `c = IL >> 2`:

- `DLT = (DETL · QM4[c]) >> 15` (inverse quantiser, block 2L).
- `NBL = ((NBL · 127) >> 7) + WL[RL42[c]]`, clamped to [0, 18432] (block 3L, LOGSCL).
- `DETL = SCALE(NBL, 8)` (block 3L, SCALEL).
- Predictor update of §4.5 with `d = DLT`.

**High band**, given `IH`:

- `DH = (DETH · QM2[IH]) >> 15`.
- `NBH = ((NBH · 127) >> 7) + WH[RH2[IH]]`, clamped to [0, 22528].
- `DETH = SCALE(NBH, 10)`.
- Predictor update of §4.5 with `d = DH`.

`SCALE(nb, base)`: `i = (nb >> 6) & 31`, `k = base − (nb >> 11)`;
`w = ILB[i] >> k` if `k ≥ 0`, else `ILB[i] << −k`; the result is `w << 2`.

### 4.5 Adaptive predictor (block 4, identical for both bands)

Input `d`. In this order:

1. `D[0] = d`; `R[0] = saturate16(S + d)`; `P[0] = saturate16(SZ + d)`.
2. **UPPOL2** — let `sg(v) = v >> 15` (sign: 0 or −1).
   `w1 = saturate16(A[1] << 2)`; `w2 = −w1` if `sg(P[0]) = sg(P[1])`, else `w1`;
   if `w2 > 32767` then `w2 = 32767`;
   `w3 = (w2 >> 7) + (128 if sg(P[0]) = sg(P[2]) else −128) + ((A[2] · 32512) >> 15)`;
   `AP[2] = clamp(w3, −12288, 12288)`.
3. **UPPOL1** — `AP[1] = saturate16((192 if sg(P[0]) = sg(P[1]) else −192) + ((A[1] · 32640) >> 15))`;
   `lim = saturate16(15360 − AP[2])`; clamp `AP[1]` to [−lim, lim].
4. **UPZERO** — `g = 0` if `d = 0`, else `128`. For i = 1..6:
   `BP[i] = saturate16((g if sg(D[i]) = sg(d) else −g) + ((B[i] · 32640) >> 15))`.
5. **DELAYA** — for i = 6 down to 1: `D[i] = D[i−1]`, `B[i] = BP[i]`;
   for i = 2 down to 1: `R[i] = R[i−1]`, `P[i] = P[i−1]`, `A[i] = AP[i]`.
6. **FILTEP** — `SP = saturate16(((A[1] · saturate16(2·R[1])) >> 15) + ((A[2] · saturate16(2·R[2])) >> 15))`.
7. **FILTEZ** — `SZ = saturate16(Σ_{i=1..6} (B[i] · saturate16(2·D[i])) >> 15)`
   (each product shifted individually, the sum saturated once).
8. **PREDIC** — `S = saturate16(SP + SZ)`.

### 4.6 Decoder, per octet

1. Split: `IL = octet & 0x3F`, `IH = (octet >> 6) & 3`.
2. **Low band reconstruction (block 5L)** — using the state *before* this octet's update:
   `RL = clamp(SL + ((DETL · QM6[IL]) >> 15), −16384, 16383)`.
3. Low-band adaptation of §4.4 with `c = IL >> 2`.
4. **High band reconstruction (block 5H)** — `DH = (DETH · QM2[IH]) >> 15`;
   `RH = clamp(SH + DH, −16384, 16383)` with `SH` from before this octet's update.
5. High-band adaptation of §4.4 with `IH`.
6. **Receive QMF** — shift the decoder's own 24-entry delay line down by two;
   `x[22] = RL + RH`, `x[23] = RL − RH`;
   `acc1 = Σ_{i=0..11} x[2i+1] · h[11−i]`, `acc2 = Σ_{i=0..11} x[2i] · h[i]`;
   emit `saturate16(acc1 >> 11)` then `saturate16(acc2 >> 11)`.

## 5. Constant tables (copy exactly — these are the Recommendation's tables)

```
Q6   = [0, 35, 72, 110, 150, 190, 233, 276, 323, 370, 422, 473, 530, 587, 650, 714,
        786, 858, 940, 1023, 1121, 1219, 1339, 1458, 1612, 1765, 1980, 2195, 2557, 2919, 0, 0]
ILN  = [0, 63, 62, 31, 30, 29, 28, 27, 26, 25, 24, 23, 22, 21, 20, 19,
        18, 17, 16, 15, 14, 13, 12, 11, 10, 9, 8, 7, 6, 5, 4, 0]
ILP  = [0, 61, 60, 59, 58, 57, 56, 55, 54, 53, 52, 51, 50, 49, 48, 47,
        46, 45, 44, 43, 42, 41, 40, 39, 38, 37, 36, 35, 34, 33, 32, 0]
WL   = [-60, -30, 58, 172, 334, 538, 1198, 3042]
RL42 = [0, 7, 6, 5, 4, 3, 2, 1, 7, 6, 5, 4, 3, 2, 1, 0]
ILB  = [2048, 2093, 2139, 2186, 2233, 2282, 2332, 2383, 2435, 2489, 2543, 2599, 2656, 2714, 2774, 2834,
        2896, 2960, 3025, 3091, 3158, 3228, 3298, 3371, 3444, 3520, 3597, 3676, 3756, 3838, 3922, 4008]
QM4  = [0, -20456, -12896, -8968, -6288, -4240, -2584, -1200,
        20456, 12896, 8968, 6288, 4240, 2584, 1200, 0]
QM2  = [-7408, -1616, 7408, 1616]
QM6  = [-136, -136, -136, -136, -24808, -21904, -19008, -16704,
        -14984, -13512, -12280, -11192, -10232, -9360, -8576, -7856,
        -7192, -6576, -6000, -5456, -4944, -4464, -4008, -3576,
        -3168, -2776, -2400, -2032, -1688, -1360, -1040, -728,
        24808, 21904, 19008, 16704, 14984, 13512, 12280, 11192,
        10232, 9360, 8576, 7856, 7192, 6576, 6000, 5456,
        4944, 4464, 4008, 3576, 3168, 2776, 2400, 2032,
        1688, 1360, 1040, 728, 432, 136, -432, -136]
IHN  = [0, 1, 0]
IHP  = [0, 3, 2]
WH   = [0, -214, 798]
RH2  = [2, 1, 2, 1]
h    = [3, -11, 12, 32, -210, 951, 3876, -805, 362, -156, 53, -11]
```

## 6. Edge cases the tests must pin

| Case | Expected |
|---|---|
| Empty input (either direction) | empty output |
| Odd sample count on encode | last sample ignored; `floor(n/2)` octets |
| Full-scale square wave ±32767 / −32768 | no overflow; every `saturate16` and both clamps engage |
| Long silence after loud signal | `NBL`/`NBH` decay to 0 and stay clamped; `DET` returns to its floor |
| `NBL` driven to 18432 / `NBH` to 22528 | clamp holds; `SCALE` takes the left-shift branch when `k < 0` |
| Encoder level search falls through | `m = 30` (`WD` above the largest threshold) |
| Decoder fed every octet 0x00..0xFF | no exception; output deterministic |

Mode 2/3 decoding, if ever added, keeps the same adaptation (which only uses `IL >> 2`) and
changes only the block 5L reconstruction table — that is why §4.6 separates the two.

## 7. Reference vectors to capture

Commit each vector with a note naming the tool, its version and the exact command.

1. **ITU-T G.722 Appendix II digital test sequences** (distributed with the Recommendation /
   ITU-T G.191 STL): the mode 1 encoder and decoder sequences. They are 14-bit and include
   reset patterns, so they pin §4.2–§4.6 bit-exactly once the §2 scaling is accounted for;
   record how the 14-bit samples were mapped.
2. **FFmpeg (LGPL-2.1-or-later, usable as an oracle and licence-compatible)**:
   `ffmpeg -f s16le -ar 16000 -ac 1 -i in.raw -c:a g722 -f g722 out.g722` and
   `ffmpeg -f g722 -ar 16000 -i in.g722 -f s16le out.raw`, on: 1 s of silence; a 1 kHz sine
   at −6 dBFS; a 6 kHz sine (high band dominant); white noise; a full-scale square wave; a
   speech clip. Expect byte-identical encoder output and sample-identical decoder output.
3. **Round-trip** of the same inputs through our encoder and decoder, checked against FFmpeg's
   decoder on our encoder's output (the interoperability direction that matters for writers).
4. **Decoder sweep**: the 256-octet stream 0x00..0xFF repeated 64 times, decoded by FFmpeg.
